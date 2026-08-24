using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using FlanderDev.RouteGen;
using System.Net;
using System.Security.Claims;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Client.Pages.Modifications;

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
            selectedVersion = await ModsApi.GetMod(Id);

            // Load all versions of the group
            var result = await ModsApi.GetVersions(selectedVersion.ModGroupId);
            // GetVersions only returns approved versions; GetMod (above) doesn't filter by
            // approval, so an owner/admin viewing their own not-yet-approved mod would
            // otherwise not see it in this list at all.
            allVersions = result.Versions.Count > 0 ? result.Versions : [selectedVersion];

            // Check ownership
            if (AuthState is not null)
            {
                var state = await AuthState;
                var userIdStr = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(userIdStr, out var userId))
                    isOwner = result.Group.OwnerId == userId;
                isAdmin = state.User.IsInRole(AppRoles.Admin);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            selectedVersion = null;
        }
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
            using var stream = await ModsApi.DownloadFile(modId, file.Id);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            await JS.InvokeVoidAsync("f3m.downloadFile", file.OriginalName, Convert.ToBase64String(ms.ToArray()));
            selectedVersion!.DownloadCount++;
            dlSuccess = true;
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
            await ModsApi.Delete(selectedVersion.Id);

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
        catch (ApiException ex)
        {
            deleteError = ex.StatusCode == HttpStatusCode.Forbidden
                ? "You don't have permission to delete this mod."
                : ex.ResponseBody ?? $"Delete failed ({(int)ex.StatusCode}).";
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
                try
                {
                    using var stream = await ModsApi.DownloadFile(selectedVersion.Id, file.Id);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    await JS.InvokeVoidAsync("f3m.downloadFile", file.OriginalName, Convert.ToBase64String(ms.ToArray()));
                    // Small delay so browser doesn't block multiple simultaneous downloads
                    await Task.Delay(400);
                }
                catch { anyError = true; }
            }
            selectedVersion.DownloadCount++;
            dlSuccess = !anyError;
            dlError = anyError;
        }
        catch { dlError = true; }
        finally { downloadingAll = false; }
    }
}