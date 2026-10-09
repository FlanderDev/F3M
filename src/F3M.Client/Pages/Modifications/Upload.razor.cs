using F3M.Client.Business;
using F3M.Client.Models;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace F3M.Client.Pages.Modifications;

public partial class Upload
{
    [Parameter]
    public int? GroupId { get; set; }

    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    private bool IsNewVersion => GroupId.HasValue;
    private ModUploadDto dto = new();
    private Mod? existingMod;

    // The dropdown reports full Mod objects (from SearchMods, one per logical mod); kept here
    // for display, then reduced to distinct ModGroupIds on dto for transmission — a dependency
    // targets the logical mod, not the specific version that happened to show up in search.
    private List<Mod> selectedDependencies = [];

    private void SetDependencies(List<Mod> selected)
    {
        selectedDependencies = selected;
        dto.DependencyGroupIds = [.. selected.Select(m => m.ModGroupId).Distinct()];
    }

    // Passed directly into ModImagePicker via @bind-*
    private byte[]? imageBytes;
    private string? previewDataUrl;
    private string imageFileName = string.Empty;
    private string[] Categories = [];

    private string ImagePickerHint => IsNewVersion ? "optional, leave blank to keep existing" : "optional";

    // Passed by reference into ModFileList; the component mutates it in place.
    private readonly List<FileEntry> fileEntries = [];

    private bool HasInvalidInstallPaths =>
        fileEntries.Any(e => !InstallPaths.TryPlan(e.OriginalName, e.InstallPath, out _));

    private bool HasInvalidGeneratedPaths =>
        GeneratedPathChecks.HasErrors(GeneratedPathChecks.Evaluate(dto.GeneratedPaths, ShippedPaths));

    // Game paths this upload installs itself: each plain file's target, and each archive entry's extracted path.
    // The pattern editor checks generated-file patterns against them. Recomputed only when the files change,
    // since listing an archive means reading its index.
    private IReadOnlyList<string> shippedPaths = [];
    private string shippedKey = string.Empty;

    private IReadOnlyList<string> ShippedPaths
    {
        get
        {
            var key = string.Join('\n', fileEntries.Select(e =>
                $"{e.OriginalName}\t{e.InstallPath}\t{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.Bytes)}"));

            if (key != shippedKey)
            {
                shippedKey = key;
                shippedPaths = ComputeShippedPaths();
            }

            return shippedPaths;
        }
    }

    private List<string> ComputeShippedPaths()
    {
        var paths = new List<string>();
        foreach (var entry in fileEntries)
        {
            if (!InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out var placement))
                continue;

            if (!placement.IsArchive)
            {
                paths.Add(placement.Target);
                continue;
            }

            var listing = ArchiveInspector.List(entry.Bytes);
            if (listing.Error is not null)
                continue;

            foreach (var archiveEntry in listing.Entries)
            {
                if (InstallPaths.TryExpand(placement.Target, archiveEntry.RelativePath, out var target))
                    paths.Add(target);
            }
        }

        return paths;
    }

    // Warnings the server returned for the last upload (overlaps with other mods' patterns, deep wildcards).
    private List<string> uploadWarnings = [];
    private string? uploadError;
    private bool uploading;
    private bool uploadSuccess;
    private int uploadedId;
    private int progress;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        Categories = [.. await ModsApi.GetCategories(), .. Configuration.DefaultCategories];

        if (GroupId is not int groupId)
            return;

        var result = await ModsApi.GetVersions(groupId);
        existingMod = result.Versions.FirstOrDefault();
        if (existingMod is not null)
        {
            dto.Name = existingMod.Name;
            dto.Category = existingMod.Category;
            dto.Description = existingMod.Description;
            dto.ModGroupId = groupId;
        }
    }

    // ── Submit ────────────────────────────────────────────────────────────────
    // Stays a raw HttpClient multipart POST — Upload isn't part of IModsApi. It binds via
    // [FromForm]/IFormFileCollection on the server, which RouteGen's [Body] (JSON only)
    // doesn't cover.
    private async Task HandleSubmit()
    {
        bool isAuthed = await AuthState.IsAuthenticatedAsync();
        if (!isAuthed)
        {
            uploadError = "You must be signed in to upload mods.";
            return;
        }

        if (fileEntries.Count == 0)
            return;

        uploading = true;
        uploadError = null;
        progress = 10;
        StateHasChanged();

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(dto.Name), nameof(ModUploadDto.Name));
            content.Add(new StringContent(dto.Version), nameof(ModUploadDto.Version));
            content.Add(new StringContent(dto.Category), nameof(ModUploadDto.Category));
            content.Add(new StringContent(dto.Description), nameof(ModUploadDto.Description));

            if (dto.ModGroupId.HasValue)
                content.Add(new StringContent(dto.ModGroupId.Value.ToString()), nameof(ModUploadDto.ModGroupId));

            foreach (var depGroupId in dto.DependencyGroupIds)
                content.Add(new StringContent(depGroupId.ToString()), nameof(ModUploadDto.DependencyGroupIds));

            if (imageBytes is not null)
            {
                var imgPart = new ByteArrayContent(imageBytes);
                imgPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(imgPart, "previewImage", imageFileName);
            }

            progress = 20; StateHasChanged();

            foreach (var entry in fileEntries)
            {
                var filePart = new ByteArrayContent(entry.Bytes);
                filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(filePart, "files", entry.OriginalName);
                content.Add(new StringContent(entry.InstallPath ?? string.Empty), "installPaths");
                content.Add(new StringContent(entry.OriginalName), "originalNames");
            }

            for (var i = 0; i < dto.GeneratedPaths.Count; i++)
            {
                var generated = dto.GeneratedPaths[i];
                content.Add(new StringContent(generated.Pattern), $"GeneratedPaths[{i}].Pattern");
                content.Add(new StringContent(generated.Kind.ToString()), $"GeneratedPaths[{i}].Kind");
            }

            progress = 50; StateHasChanged();

            var response = await Http.PostAsync("api/mods/upload", content);
            progress = 90; StateHasChanged();

            if (response.IsSuccessStatusCode)
            {
                var uploaded = await response.Content.ReadFromJsonAsync<Mod>();
                uploadedId = uploaded?.Id ?? 0;
                uploadSuccess = true;
                progress = 100;

                if (response.Headers.TryGetValues("X-F3M-Warnings", out var warningValues))
                {
                    uploadWarnings = Uri.UnescapeDataString(string.Concat(warningValues))
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                }
            }
            else
            {
                uploadError = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Session expired. Please sign in again."
                    : await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex) { uploadError = ex.Message; }
        finally { uploading = false; }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────
    private void Reset()
    {
        dto = new();
        selectedDependencies = [];
        fileEntries.Clear();
        imageBytes = null;
        imageFileName = string.Empty;
        previewDataUrl = null;
        uploadError = null;
        uploadSuccess = false;
        uploadWarnings = [];
        uploading = false;
        progress = 0;
        uploadedId = 0;
    }

    private async Task<List<Mod>?> LoadMultiSelectDropdownValues(string searchText)
        => await ModsApi.SearchMods(searchText);
}
