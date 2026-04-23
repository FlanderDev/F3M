using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Net.Http.Json;

namespace F3M.Client.Pages;

public partial class ModDetail
{
    [Parameter] public int Id { get; set; }

    private Mod? selectedVersion;
    private List<Mod> allVersions = [];
    private bool loading = true;
    private bool isOwner;
    private bool isAdmin;

    private bool showDeleteConfirm;
    private bool deleting;
    private string? deleteError;

    private int? activeFileId;
    private int? downloadingFileId;
    private bool downloadingAll;
    private bool dlSuccess;
    private bool dlError;

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // Load the requested version
            selectedVersion = await Http.GetFromJsonAsync<Mod>($"api/mods/{Id}");
            if (selectedVersion is null) return;

            // Load all versions of the group
            var result = await Http.GetFromJsonAsync<ModVersionsResult>(
                $"api/mods/group/{selectedVersion.ModGroupId}/versions");
            allVersions = result?.Versions ?? [selectedVersion];

            // Check ownership
            if (AuthState is not null)
            {
                var state = await AuthState;
                Console.WriteLine($"OwnerId: {result?.Group.OwnerId}");
                foreach (var c in state.User.Claims)
                    if (c.Type == "sub") Console.WriteLine($"Claim: {c.Type}: {c.Value}");
                var userIdStr = state.User.FindFirst(
                    "sub")?.Value;
                if (int.TryParse(userIdStr, out var userId))
                    isOwner = result?.Group.OwnerId == userId;
                isAdmin = state.User.IsInRole("Admin");
            }
        }
        catch { selectedVersion = null; }
        finally { loading = false; }
    }

    private void SelectVersion(Mod ver)
    {
        selectedVersion = ver;
        dlSuccess = false;
        dlError = false;
        activeFileId = null;
    }

    private async Task DownloadFile(int modId, ModFile file)
    {
        downloadingFileId = file.Id;
        dlSuccess = false; dlError = false;
        try
        {
            var resp = await Http.PostAsync($"api/mods/{modId}/download/{file.Id}", null);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                await JS.InvokeVoidAsync("f3m.downloadFile", file.OriginalName, Convert.ToBase64String(bytes));
                selectedVersion!.DownloadCount++;
                dlSuccess = true;
            }
            else dlError = true;
        }
        catch { dlError = true; }
        finally { downloadingFileId = null; }
    }

    private async Task DeleteVersion()
    {
        if (selectedVersion is null) return;
        deleting = true; deleteError = null;
        try
        {
            var resp = await Http.DeleteAsync($"api/mods/{selectedVersion.Id}");
            if (resp.IsSuccessStatusCode)
            {
                // If it was the last version, go home; otherwise reload versions
                if (allVersions.Count <= 1)
                    Nav.NavigateTo("/");
                else
                {
                    allVersions.Remove(selectedVersion);
                    selectedVersion = allVersions[0];
                    showDeleteConfirm = false;
                }
            }
            else
            {
                deleteError = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "You don't have permission to delete this mod."
                    : $"Delete failed ({(int)resp.StatusCode}).";
            }
        }
        catch (Exception ex) { deleteError = ex.Message; }
        finally { deleting = false; }
    }

    private async Task DownloadAll()
    {
        if (selectedVersion is null) return;
        downloadingAll = true;
        dlSuccess = false; dlError = false;
        try
        {
            bool anyError = false;
            foreach (var file in selectedVersion.Files)
            {
                var resp = await Http.PostAsync($"api/mods/{selectedVersion.Id}/download/{file.Id}", null);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    await JS.InvokeVoidAsync("f3m.downloadFile", file.OriginalName, Convert.ToBase64String(bytes));
                    // Small delay so browser doesn't block multiple simultaneous downloads
                    await Task.Delay(400);
                }
                else { anyError = true; }
            }
            selectedVersion.DownloadCount++;
            dlSuccess = !anyError;
            dlError = anyError;
        }
        catch { dlError = true; }
        finally { downloadingAll = false; }
    }
}