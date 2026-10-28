using F3M.Client.Shared;
using F3M.Shared.Models;

namespace F3M.Client.Pages.Managment;

public partial class AdminUsers
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
            (u.F95Username?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    protected override async Task OnInitializedAsync()
    {
        try
        {
            users = await Api.GetUsersAsync();
        }
        catch (Exception ex)
        {
            loadError = ApiErrors.Describe(ex);
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
        catch (Exception ex) { actionError = ApiErrors.Describe(ex); }
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
        catch (Exception ex) { actionError = ApiErrors.Describe(ex); }
        finally { busyId = null; }
    }
}
