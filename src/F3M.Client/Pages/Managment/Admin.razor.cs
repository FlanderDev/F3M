using F3M.Shared.Api;
using F3M.Shared.Models;
using FlanderDev.RouteGen;
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