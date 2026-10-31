using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Net;
using System.Security.Claims;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Client.Pages.Modifications;

public partial class ModDetail
{
    [Parameter] public int Id { get; set; }

    private Mod? selectedVersion;
    private List<Mod> allVersions = [];
    private ModGroup? group;

    // Whose profiles the author line links to; null while an imported mod is unclaimed.
    private ModOwnerDto? owner;
    private bool loading = true;
    private bool isOwner;
    private bool isAdmin;
    private int? currentUserId;

    private bool showDeleteConfirm;
    private bool showRemoveConfirm;
    private bool deleting;
    private string? deleteError;

    private bool claiming;
    private bool claimSucceeded;
    private string? claimMessage;

    // Admin: who the mod belongs to.
    private List<AdminUserDto> users = [];
    private int assignUserId;
    private bool assigning;
    private string? assignMessage;

    private int? activeFileId;
    private int? downloadingFileId;
    private bool downloadingAll;
    private bool dlSuccess;
    private bool dlError;

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    /// <summary>A suggested tag was approved: every version shows it as the mod's own now.</summary>
    private void OnTagApproved(int tagId)
    {
        // Like the server: the new tag replaces the mod's tags it's a kind of.
        var replaced = Tags.Graph.SelfAndAncestors(tagId).Where(id => id != tagId && !Tags.IsType(id)).ToHashSet();
        foreach (var version in allVersions.Append(selectedVersion).OfType<Mod>().Distinct())
        {
            version.TagIds.RemoveAll(replaced.Contains);
            if (!version.TagIds.Contains(tagId))
                version.TagIds.Add(tagId);
            version.CommunityTagIds.Remove(tagId);
        }
    }

    // The id the page shows. Following a link to another mod (a requirement) keeps this component and only changes
    // Id, so the page loads whenever Id differs from it, not just once.
    private int? loadedId;

    protected override async Task OnParametersSetAsync()
    {
        if (loadedId == Id)
            return;
        loadedId = Id;

        loading = true;
        isOwner = false;
        showDeleteConfirm = showRemoveConfirm = false;
        deleteError = claimMessage = assignMessage = null;
        activeFileId = null;
        dlSuccess = dlError = false;

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
            group = result.Group;
            owner = result.Owner;
            assignUserId = group.OwnerId;

            // Check ownership
            if (AuthState is not null)
            {
                var state = await AuthState;
                var userIdStr = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(userIdStr, out var userId))
                {
                    currentUserId = userId;
                    isOwner = result.Group.OwnerId == userId;
                }
                isAdmin = state.User.IsInRole(AppRoles.Admin);
            }

            if (isAdmin && users.Count == 0)
                users = await AdminApi.GetUsersAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            selectedVersion = null;
        }
        finally { loading = false; }
    }

    /// <summary>
    /// The uploader's F95zone profile: the owner's linked account, or for an import still unclaimed, the F95 user
    /// who posted it. Null when neither is known.
    /// </summary>
    private (string Url, string Name)? F95Link
    {
        get
        {
            if (owner?.F95UserId is { Length: > 0 } ownerId)
                return (F95Profile.LinkFor(owner.F95Username, ownerId), owner.F95Username ?? ownerId);
            if (owner is null && group?.F95OwnerUserId is { Length: > 0 } posterId)
                return (F95Profile.LinkFor(group.F95OwnerName, posterId), group.F95OwnerName ?? posterId);
            return null;
        }
    }

    /// <summary>Tooltip of the cross next to the author: the mod belongs to nobody on F3M yet.</summary>
    private string UnregisteredHint =>
        group?.F95OwnerName is { Length: > 0 } uploader
            ? $"Not registered on F3M yet. {uploader} gets this mod by signing in with their F95zone account."
            : "Not registered on F3M yet.";

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
                Nav.NavigateTo(Paths.Index);
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

    private string UnclaimedLabel =>
        group?.F95OwnerName is { Length: > 0 } name ? $"Unclaimed (goes to {name} on F95 sign-in)" : "Unclaimed";

    private void AskDeleteVersion()
    {
        // Deleting the only version removes the mod; that goes through the whole-mod removal, which also works while
        // other mods depend on it.
        showDeleteConfirm = allVersions.Count > 1;
        showRemoveConfirm = allVersions.Count <= 1;
        deleteError = null;
    }

    private void AskRemoveMod()
    {
        showRemoveConfirm = true;
        showDeleteConfirm = false;
        deleteError = null;
    }

    private async Task RemoveMod()
    {
        if (group is null) return;
        deleting = true; deleteError = null;
        try
        {
            await ModsApi.DeleteGroup(group.Id);
            Nav.NavigateTo(Paths.Index);
        }
        catch (ApiException ex)
        {
            deleteError = ex.StatusCode == HttpStatusCode.Forbidden
                ? "You don't have permission to remove this mod."
                : ex.ResponseBody ?? $"Removing failed ({(int)ex.StatusCode}).";
        }
        catch (Exception ex) { deleteError = ex.Message; }
        finally { deleting = false; }
    }

    private async Task Claim()
    {
        if (group is null) return;
        claiming = true; claimMessage = null;
        try
        {
            var result = await ModsApi.Claim(group.Id);
            claimSucceeded = result.Success;
            claimMessage = result.Message;
            if (result.Success)
            {
                // Reload so the owner actions appear.
                var refreshed = await ModsApi.GetVersions(group.Id);
                group = refreshed.Group;
                owner = refreshed.Owner;
                isOwner = true;
            }
        }
        catch (ApiException ex)
        {
            claimSucceeded = false;
            claimMessage = ex.StatusCode == HttpStatusCode.Unauthorized
                ? "Please sign in first."
                : ex.ResponseBody ?? $"Claiming failed ({(int)ex.StatusCode}).";
        }
        catch (Exception ex) { claimSucceeded = false; claimMessage = ex.Message; }
        finally { claiming = false; }
    }

    private async Task AssignOwner()
    {
        if (group is null) return;
        assigning = true; assignMessage = null;
        try
        {
            var userId = assignUserId == ModGroup.UnclaimedOwnerId ? (int?)null : assignUserId;
            group = await AdminApi.AssignModOwnerAsync(group.Id, new AssignModOwnerDto { UserId = userId });
            owner = (await ModsApi.GetVersions(group.Id)).Owner;
            assignUserId = group.OwnerId;
            isOwner = group.OwnerId == currentUserId;
            assignMessage = userId is null
                ? "The mod is unclaimed again."
                : $"The mod now belongs to {users.FirstOrDefault(u => u.Id == userId)?.Username ?? "that user"}.";
        }
        catch (ApiException ex) { assignMessage = ex.ResponseBody ?? $"Saving failed ({(int)ex.StatusCode})."; }
        catch (Exception ex) { assignMessage = ex.Message; }
        finally { assigning = false; }
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