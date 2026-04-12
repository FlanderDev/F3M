using F3M.Shared.Models;
using System.Net.Http.Json;

namespace F3M.Client.Pages;

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
            users = await Http.GetFromJsonAsync<List<AdminUserDto>>("api/admin/users") ?? [];
        }
        catch (Exception ex) { loadError = ex.Message; }
        finally { loading = false; }
    }

    private async Task ToggleAdmin(AdminUserDto user)
    {
        busyId = user.Id; actionError = null;
        try
        {
            var resp = await Http.PostAsync($"api/admin/users/{user.Id}/toggle-admin", null);
            if (resp.IsSuccessStatusCode)
            {
                var updated = await resp.Content.ReadFromJsonAsync<AdminUserDto>();
                if (updated is not null)
                {
                    var idx = users.FindIndex(u => u.Id == user.Id);
                    if (idx >= 0) users[idx] = updated;
                }
            }
            else
            {
                actionError = $"Failed: {await resp.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex) { actionError = ex.Message; }
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
            var resp = await Http.DeleteAsync($"api/admin/users/{deleteTarget.Id}");
            if (resp.IsSuccessStatusCode)
            {
                users.RemoveAll(u => u.Id == deleteTarget.Id);
                deleteTarget = null;
            }
            else
            {
                actionError = await resp.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex) { actionError = ex.Message; }
        finally { busyId = null; }
    }
}