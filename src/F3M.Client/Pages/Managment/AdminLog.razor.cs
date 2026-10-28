using F3M.Client.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;

namespace F3M.Client.Pages.Managment;

public partial class AdminLog
{
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

    protected override Task OnInitializedAsync() => ReloadLog();

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
                logError = ApiErrors.Describe(ex);
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
}
