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

    // Error reports: the first page loads with the page, more on request.
    private const int ErrorPageSize = 25;
    private readonly List<Telemetry.ErrorReport> errors = [];
    private int errorTotal;
    private bool loadingErrors;
    private string? errorsError;

    private IEnumerable<AdminUserDto> Filtered => string.IsNullOrWhiteSpace(search)
        ? users
        : users.Where(u =>
            u.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            u.Email.Contains(search, StringComparison.OrdinalIgnoreCase));

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

        await ReloadErrors();
    }

    private async Task ReloadErrors()
    {
        errors.Clear();
        errorTotal = 0;
        await LoadErrorsAsync(ErrorPageSize);
    }

    private Task LoadMoreErrors() => LoadErrorsAsync(ErrorPageSize);

    /// <summary>The rest, page by page; new reports arriving meanwhile only raise the total.</summary>
    private Task LoadAllErrors() => LoadErrorsAsync(int.MaxValue);

    private async Task LoadErrorsAsync(int count)
    {
        loadingErrors = true;
        errorsError = null;
        try
        {
            var wanted = count;
            do
            {
                var take = Math.Min(wanted, IAdminApi.MaxErrorReportsPerPage);
                var page = await Api.GetErrorReportsAsync(errors.Count, take);
                errorTotal = page.Total;

                // Reports that arrived since the last page push older ones down; skip any already shown.
                var known = errors.Select(e => e.Id).ToHashSet();
                errors.AddRange(page.Items.Where(e => known.Add(e.Id)));
                wanted -= take;

                if (page.Items.Count < take)
                    break;
                StateHasChanged();
            }
            while (wanted > 0 && errors.Count < errorTotal);
        }
        catch (Exception ex)
        {
            errorsError = DescribeError(ex);
        }
        finally
        {
            loadingErrors = false;
        }
    }

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