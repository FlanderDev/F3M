using F3M.Shared.Api;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

public partial class Admin
{
    private List<AdminUserDto> users = [];
    private bool loading = true;
    private string? loadError;
    private string? actionError;
    private string search = string.Empty;
    private int? busyId;
    private AdminUserDto? deleteTarget;
    private bool rebuilding;
    private CatalogRebuildResult? rebuildResult;
    private string? rebuildError;

    // The site log: the first page loads with the page, more on request.
    private const int LogPageSize = 25;
    private readonly List<LogEntry> logEntries = [];
    private int logTotal;
    private bool loadingLog;
    private string? logError;
    private LogSeverity logMinLevel = LogSeverity.Information;
    private LogSourceFilter logSource = LogSourceFilter.All;
    private string logSearch = string.Empty;

    // Bumped by every reload, so a slower earlier load can't add its entries to the new list.
    private int logGeneration;

    private IEnumerable<AdminUserDto> Filtered => string.IsNullOrWhiteSpace(search)
        ? users
        : users.Where(u =>
            u.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (u.F95Username?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    protected override async Task OnInitializedAsync()
    {
        try
        {
            users = await Api.GetUsersAsync();
        }
        catch (Exception ex)
        {
            loadError = DescribeError(ex);
        }
        finally { loading = false; }

        await ReloadLog();
    }

    private async Task ReloadLog()
    {
        logGeneration++;
        logEntries.Clear();
        logTotal = 0;
        await LoadLogAsync(LogPageSize);
    }

    private Task OnLogMinLevelChanged(LogSeverity level)
    {
        logMinLevel = level;
        return ReloadLog();
    }

    private Task OnLogSourceChanged(LogSourceFilter source)
    {
        logSource = source;
        return ReloadLog();
    }

    private async Task OnLogSearchKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await ReloadLog();
    }

    private Task LoadMoreLog() => LoadLogAsync(LogPageSize);

    /// <summary>The rest, page by page; new entries arriving meanwhile only raise the total.</summary>
    private Task LoadAllLog() => LoadLogAsync(int.MaxValue);

    private async Task LoadLogAsync(int count)
    {
        var generation = logGeneration;
        loadingLog = true;
        logError = null;
        try
        {
            var wanted = count;
            do
            {
                var take = Math.Min(wanted, IAdminApi.MaxLogEntriesPerPage);
                var page = await Api.GetLogAsync(logEntries.Count, take, logMinLevel, logSource, logSearch.Trim());
                if (generation != logGeneration)
                    return; // The filter changed meanwhile; the new load owns the list.
                logTotal = page.Total;

                // Entries that arrived since the last page push older ones down; skip any already shown.
                var known = logEntries.Select(e => e.Id).ToHashSet();
                logEntries.AddRange(page.Items.Where(e => known.Add(e.Id)));
                wanted -= take;

                if (page.Items.Count < take)
                    break;
                StateHasChanged();
            }
            while (wanted > 0 && logEntries.Count < logTotal);
        }
        catch (Exception ex)
        {
            if (generation == logGeneration)
                logError = DescribeError(ex);
        }
        finally
        {
            if (generation == logGeneration)
                loadingLog = false;
        }
    }

    private static string LevelBadge(LogSeverity level) => level switch
    {
        LogSeverity.Critical or LogSeverity.Error => "bg-danger",
        LogSeverity.Warning => "bg-warning text-dark",
        _ => "bg-secondary"
    };

    private static string LevelName(LogSeverity level) => level switch
    {
        LogSeverity.Information => "Info",
        _ => level.ToString()
    };

    /// <summary>"F3M.Server.Services.ModsService" → "ModsService"; the full name is in the tooltip.</summary>
    private static string ShortCategory(string? category) =>
        category is { Length: > 0 } ? category[(category.LastIndexOf('.') + 1)..] : string.Empty;

    private async Task ToggleAdmin(AdminUserDto user)
    {
        busyId = user.Id; actionError = null;
        try
        {
            var updated = await Api.ToggleAdminAsync(user.Id);
            var idx = users.FindIndex(u => u.Id == user.Id);
            if (idx >= 0) users[idx] = updated;
        }
        catch (Exception ex) { actionError = DescribeError(ex); }
        finally { busyId = null; }
    }

    private void ConfirmDelete(AdminUserDto user)
    {
        deleteTarget = user;
        actionError = null;
    }

    private async Task ExecuteDelete()
    {
        if (deleteTarget is null) return;
        busyId = deleteTarget.Id; actionError = null;
        try
        {
            await Api.DeleteUserAsync(deleteTarget.Id);
            users.RemoveAll(u => u.Id == deleteTarget.Id);
            deleteTarget = null;
        }
        catch (Exception ex) { actionError = DescribeError(ex); }
        finally { busyId = null; }
    }

    private async Task RebuildCatalog()
    {
        rebuilding = true; rebuildError = null; rebuildResult = null;
        try
        {
            rebuildResult = await Api.RebuildCatalogAsync();
        }
        catch (Exception ex) { rebuildError = DescribeError(ex); }
        finally { rebuilding = false; }
    }

    /// <summary>
    /// ApiException.Message is a generic "API call failed with status 400" string — the actual
    /// server-provided detail (e.g. "You cannot delete your own account.") is in ResponseBody,
    /// JSON-serialized as a plain string by BadRequest(ex.Message) on the server.
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        if (ex is not ApiException { ResponseBody: { Length: > 0 } body })
            return ex.Message;

        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}