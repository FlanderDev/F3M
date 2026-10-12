using F3M.Client.Business;
using F3M.Client.Components;
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
    // The page adds the mod a file's type needs (the framework, a loader) to the same list.
    private List<MultiSelectDropdown<Mod>.InternalItem> dependencyItems = [];

    private void OnDependenciesChanged(List<MultiSelectDropdown<Mod>.InternalItem> selected)
    {
        dependencyItems = selected;
        dto.DependencyGroupIds = [.. selected.Select(i => i.Model.ModGroupId).Distinct()];
    }

    // ── Types: category, required mods, warnings ──────────────────────────────

    // The author picked a category; the files' types no longer change it.
    private bool categoryChosen;

    // Required mods by name (ModTemplate.RequiredModName), looked up once; null when the site doesn't have it.
    private readonly Dictionary<string, Mod?> requiredMods = new(StringComparer.OrdinalIgnoreCase);

    // Required mods added on the author's behalf once; removing one again is their call.
    private readonly HashSet<string> requirementsAdded = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<ModTemplate> UsedTemplates =>
        fileEntries.Select(e => ModTemplates.Find(e.Template)).OfType<ModTemplate>().DistinctBy(t => t.Id);

    /// <summary>After the files or their types change: fill in the category and the required mods.</summary>
    private async Task OnFilesChanged()
    {
        if (!categoryChosen && !IsNewVersion
            && UsedTemplates.FirstOrDefault(t => t.Id != ModTemplates.Custom) is { } main)
        {
            dto.Category = main.Category;
        }

        foreach (var name in UsedTemplates.Select(t => t.RequiredModName).OfType<string>().Distinct().ToList())
        {
            if (!requiredMods.ContainsKey(name))
            {
                var found = await ModsApi.SearchMods(name);
                requiredMods[name] = found?.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            if (requiredMods[name] is not { } required || requirementsAdded.Contains(name) || IsRequirementOf(required))
                continue;

            requirementsAdded.Add(name);
            if (!dependencyItems.Any(i => i.Model.ModGroupId == required.ModGroupId))
                OnDependenciesChanged([.. dependencyItems, new MultiSelectDropdown<Mod>.InternalItem(required, false)]);
        }

        StateHasChanged();
    }

    /// <summary>A new version of the required mod itself does not need it.</summary>
    private bool IsRequirementOf(Mod required) => GroupId == required.ModGroupId;

    /// <summary>
    /// Everything about the files' types the author should look at before publishing: per file, and the required mods
    /// that are on the site but not listed. The server checks the same again.
    /// </summary>
    private List<string> TypeWarnings
    {
        get
        {
            var warnings = fileEntries.SelectMany(e => e.TypeWarnings.Select(w => $"{e.OriginalName}: {w}")).ToList();
            foreach (var template in UsedTemplates.Where(t => t.RequiredModName is not null))
            {
                if (requiredMods.GetValueOrDefault(template.RequiredModName!) is { } required
                    && !IsRequirementOf(required)
                    && !dependencyItems.Any(i => i.Model.ModGroupId == required.ModGroupId))
                {
                    warnings.Add($"{template.Name} files need {template.RequiredModName}. Add it as a requirement so that players get it too.");
                }
            }

            // What the server found that the form didn't (it has the final say).
            foreach (var warning in serverWarnings.Where(w => !warnings.Contains(w)))
                warnings.Add(warning);
            return warnings;
        }
    }

    // The warnings the author accepted; a change to them needs a new confirmation.
    private string? acceptedWarnings;
    private List<string> serverWarnings = [];

    private bool WarningsAccepted
    {
        get => TypeWarnings is { Count: > 0 } warnings && acceptedWarnings == string.Join('\n', warnings);
        set => acceptedWarnings = value ? string.Join('\n', TypeWarnings) : null;
    }

    private bool NeedsConfirmation => TypeWarnings.Count > 0 && !WarningsAccepted;

    // Passed directly into ModImagePicker via @bind-*
    private byte[]? imageBytes;
    private string? previewDataUrl;
    private string imageFileName = string.Empty;
    private string[] Categories = [];

    private string ImagePickerHint => IsNewVersion ? "optional, leave blank to keep existing" : "optional";

    // Passed by reference into ModFileList; the component mutates it in place.
    private readonly List<FileEntry> fileEntries = [];

    private bool HasInvalidInstallPaths => UploadFileChecks.HasProblems(fileEntries);

    private bool HasInvalidGeneratedPaths =>
        GeneratedPathChecks.HasErrors(GeneratedPathChecks.Evaluate(dto.GeneratedPaths, ShippedPaths));

    // Game paths this upload installs itself: each plain file's target, and each archive entry's extracted path
    // (inside its archive folder). The pattern editor checks generated-file patterns against them.
    private IReadOnlyList<string> ShippedPaths => [.. fileEntries.SelectMany(e => e.Targets ?? [])];

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

            if (WarningsAccepted)
                content.Add(new StringContent("true"), nameof(ModUploadDto.AcceptWarnings));

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
                content.Add(new StringContent(entry.ArchiveRoot), "archiveRoots");
                content.Add(new StringContent(entry.Template), "templates");
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
            else if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                // The server found things about the files' types to confirm first; nothing was stored.
                var result = await response.Content.ReadFromJsonAsync<UploadWarningsResult>();
                serverWarnings = result?.Warnings ?? [];
                acceptedWarnings = null;
                uploadError = "Please look at the warnings about the files' types and confirm them.";
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
        dependencyItems = [];
        fileEntries.Clear();
        categoryChosen = false;
        requirementsAdded.Clear();
        acceptedWarnings = null;
        serverWarnings = [];
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
