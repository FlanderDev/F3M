using F3M.Client.Business;
using F3M.Client.Components;
using F3M.Client.Models;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

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

    /// <summary>After the files or their types change: fill in the category, the required mods and the suggested optional groups.</summary>
    private async Task OnFilesChanged()
    {
        RefreshSuggestedGroups();

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
            var installed = OptionalCheck.Normalized;
            var warnings = fileEntries.SelectMany(e => e.TypeWarningsWith(installed).Select(w => $"{e.OriginalName}: {w}")).ToList();
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

    // ── Optional groups ───────────────────────────────────────────────────────

    // Edited in place by the groups editor and by the install preview.
    private readonly List<OptionalGroupDraft> optionalGroups = [];

    // Suggestions the author removed; they are not suggested again for this upload.
    private readonly HashSet<string> dismissedSuggestions = new(StringComparer.OrdinalIgnoreCase);

    // Read several times per render; evaluated again only when the groups or the files change.
    private (string Key, OptionalGroupsCheck Check)? optionalCheck;

    private OptionalGroupsCheck OptionalCheck
    {
        get
        {
            var key = string.Join('\u0001', optionalGroups.Select(g => $"{g.Name}\u0002{string.Join('\u0002', g.Patterns)}"))
                      + "\u0003" + string.Join('\u0001', fileEntries.Select(e => $"{e.OriginalName}|{e.InstallPath}|{e.ArchiveRoot}|{e.Size}"));
            if (optionalCheck is not { } cached || cached.Key != key)
                optionalCheck = cached = (key, OptionalGroupChecks.Evaluate(optionalGroups, [.. ShippedPaths]));
            return cached.Check;
        }
    }

    private bool HasInvalidOptionalGroups => OptionalCheck.Error is not null;

    /// <summary>
    /// Suggests readmes and examples as optional groups (<see cref="ModTemplates.SuggestOptionalGroups(IReadOnlyList{OptionalGroupDto}, IReadOnlyCollection{string}, IEnumerable{string})"/>).
    /// Untouched suggestions follow the files; groups the author made or edited stay as they are.
    /// </summary>
    private void RefreshSuggestedGroups()
    {
        optionalGroups.RemoveAll(g => g.IsSuggestion);
        var own = optionalGroups.Select(g => g.ToDto()).ToList();
        foreach (var suggestion in ModTemplates.SuggestOptionalGroups(own, [.. ShippedPaths], dismissedSuggestions))
            optionalGroups.Add(new OptionalGroupDraft { Name = suggestion.Name, Patterns = suggestion.Patterns, IsSuggestion = true });
    }

    // Warnings the server returned for the last upload (overlaps with other mods' patterns, deep wildcards).
    private List<string> uploadWarnings = [];
    private string? uploadError;
    private bool uploading;
    private bool uploadSuccess;
    private int uploadedId;

    // How much of the request the browser has sent; uploadTotal stays 0 while the files are being prepared.
    private long uploadSent;
    private long uploadTotal;

    private int UploadPercent => uploadTotal > 0 ? (int)(uploadSent * 100 / uploadTotal) : 0;

    private string UploadStatus =>
        uploadTotal == 0 ? "Preparing the files…"
        : uploadSent < uploadTotal ? $"Uploading… {uploadSent.FileSize()} of {uploadTotal.FileSize()}"
        : "Uploaded. The server is checking the files…";

    /// <summary>Called by f3m.uploadWithProgress as the browser sends the request.</summary>
    [JSInvokable]
    public void OnUploadProgress(long sent, long total)
    {
        uploadSent = sent;
        uploadTotal = total;
        StateHasChanged();
    }

    private sealed record UploadResponse(int Status, string Body, string? Warnings);

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        // The default categories first, then any other the site already uses; each once.
        Categories = [.. F3M.Shared.Helpers.Categories.OthersLast(Configuration.DefaultCategories.Concat(await ModsApi.GetCategories()))];

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

            // A new version usually ships the same extras: start from the latest version's groups.
            foreach (var group in existingMod.OptionalGroups)
                optionalGroups.Add(new OptionalGroupDraft { Name = group.Name, Patterns = [.. group.Patterns] });
        }
    }

    // ── Submit ────────────────────────────────────────────────────────────────
    // Stays a hand-built multipart POST — Upload isn't part of IModsApi. It binds via
    // [FromForm]/IFormFileCollection on the server, which RouteGen's [Body] (JSON only)
    // doesn't cover. It is sent through f3m.uploadWithProgress (XMLHttpRequest), because
    // HttpClient in the browser uses fetch, which can't report how much has been sent.
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
        uploadSent = uploadTotal = 0;
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

            var groups = OptionalCheck.Normalized;
            for (var i = 0; i < groups.Count; i++)
            {
                content.Add(new StringContent(groups[i].Name), $"OptionalGroups[{i}].Name");
                for (var j = 0; j < groups[i].Patterns.Count; j++)
                    content.Add(new StringContent(groups[i].Patterns[j]), $"OptionalGroups[{i}].Patterns[{j}]");
            }

            // The same copy of the body HttpClient would make before handing it to the browser.
            var body = await content.ReadAsByteArrayAsync();
            uploadTotal = body.Length;
            StateHasChanged();

            using var progressTarget = DotNetObjectReference.Create(this);
            var response = await JS.InvokeAsync<UploadResponse>("f3m.uploadWithProgress", CancellationToken.None,
                Nav.ToAbsoluteUri("api/mods/upload").ToString(), content.Headers.ContentType!.ToString(), body, progressTarget);
            uploadSent = uploadTotal;

            if (response.Status is >= 200 and < 300)
            {
                var uploaded = JsonSerializer.Deserialize<Mod>(response.Body, JsonOptions);
                uploadedId = uploaded?.Id ?? 0;
                uploadSuccess = true;

                if (response.Warnings is { Length: > 0 } warningHeader)
                {
                    uploadWarnings = Uri.UnescapeDataString(warningHeader)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                }
            }
            else if (response.Status == (int)HttpStatusCode.UnprocessableEntity)
            {
                // The server found things about the files' types to confirm first; nothing was stored.
                var result = JsonSerializer.Deserialize<UploadWarningsResult>(response.Body, JsonOptions);
                serverWarnings = result?.Warnings ?? [];
                acceptedWarnings = null;
                uploadError = "Please look at the warnings about the files' types and confirm them.";
            }
            else
            {
                uploadError = response.Status switch
                {
                    (int)HttpStatusCode.Unauthorized => "Session expired. Please sign in again.",
                    (int)HttpStatusCode.RequestEntityTooLarge => $"The upload is too large. All files together can be at most {Configuration.MaxTotalSize.FileSize()}.",
                    _ => string.IsNullOrWhiteSpace(response.Body) ? $"The upload failed (HTTP {response.Status})." : response.Body,
                };
            }
        }
        catch (JSException) { uploadError = "The upload was interrupted. Check your connection and try again."; }
        catch (Exception ex) { uploadError = ex.Message; }
        finally { uploading = false; }
    }

    // What HttpClient's ReadFromJsonAsync uses.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Reset ─────────────────────────────────────────────────────────────────
    private void Reset()
    {
        dto = new();
        dependencyItems = [];
        fileEntries.Clear();
        optionalGroups.Clear();
        dismissedSuggestions.Clear();
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
        uploadSent = uploadTotal = 0;
        uploadedId = 0;
    }

    private async Task<List<Mod>?> LoadMultiSelectDropdownValues(string searchText)
        => await ModsApi.SearchMods(searchText);
}
