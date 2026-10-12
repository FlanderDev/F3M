using F3M.Client.Business;
using F3M.Client.Models;
using F3M.Client.Services;
using F3M.Shared;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace F3M.Client.Components;

public partial class PickerModFiles
{
    // ── Parameters ────────────────────────────────────────────────────────────

    /// <summary>
    /// The live list of entries. The component mutates this list in place.
    /// Pass the same list instance on every render — do not recreate it.
    /// </summary>
    [Parameter, EditorRequired]
    public List<FileEntry> Entries { get; set; } = [];

    /// <summary>
    /// Raised after any mutation (add / remove / path change) so the parent
    /// can call StateHasChanged() or run validation if needed.
    /// </summary>
    [Parameter] public EventCallback OnEntriesChanged { get; set; }

    /// <summary>The mod's name, which names a mission folder when an archive has loose .code files.</summary>
    [Parameter] public string? ModName { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    public string? Error { get; private set; }
    private int _addFileKey = 0;
    private bool _fsdOpen = false;
    private int _fsdTargetIndex = -1;

    private const long MaxMod = 512L * 1024 * 1024;

    // ── File selection ────────────────────────────────────────────────────────

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        Error = null;
        var f = e.File;
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();

        if (!Configuration.AllowedFileExtension.Contains(ext))
        {
            Error = $"File type '{ext}' not allowed.";
            return;
        }
        if (f.Size > MaxMod)
        {
            Error = "File exceeds 512 MB.";
            return;
        }

        // Buffer bytes immediately while _blazorFilesById still knows this file.
        await using var stream = f.OpenReadStream(MaxMod);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();

        // Reading an archive's index is the expensive part: done once here, and the type, the install path and the
        // archive folder are filled in from what is inside.
        var listing = InstallPaths.IsArchive(f.Name) ? ArchiveInspector.List(bytes) : null;
        var suggestion = ModTemplates.Suggest(f.Name, listing?.Entries.Select(x => x.RelativePath).ToList(), ModName);

        Entries.Add(new FileEntry
        {
            OriginalName = f.Name,
            Size = f.Size,
            Bytes = bytes,
            Listing = listing,
            Suggestion = suggestion,
            Template = suggestion.TemplateId,
            InstallPath = suggestion.InstallPath,
            ArchiveRoot = suggestion.ArchiveRoot,
        });

        // Rotate key → fresh <input type=file> DOM node for the next pick.
        _addFileKey++;

        await OnEntriesChanged.InvokeAsync();
    }

    // ── Where will it go ──────────────────────────────────────────────────────

    private static bool IsValidPath(FileEntry entry) => UploadFileChecks.Problem(entry) is null;

    /// <summary>One line under each file: where it ends up in the game folder, or what is wrong with its paths.</summary>
    private static string TargetHint(FileEntry entry)
    {
        if (UploadFileChecks.Problem(entry) is { } problem)
            return problem;

        InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out var placement);
        var isDefault = string.IsNullOrWhiteSpace(entry.InstallPath);
        if (placement.IsArchive)
        {
            var folder = placement.Target.Length == 0 ? "the game folder" : placement.Target + "/";
            var source = entry.ArchiveRoot.Length == 0 ? "Will be extracted" : $"The archive's “{entry.ArchiveRoot}” folder will be extracted";
            return $"{source} into {folder}{(isDefault ? " (no path set)" : string.Empty)}";
        }

        return $"Will be installed as {placement.Target}{(isDefault ? " (no path set: default plugin folder)" : string.Empty)}";
    }

    // ── Type ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new type brings its own install path unless the author set one: the recognised paths when it is the type the
    /// form recognised, else the type's usual folder.
    /// </summary>
    private async Task SetTemplate(FileEntry entry, string? templateId)
    {
        if (ModTemplates.Find(templateId) is not { } template)
            return;

        entry.Template = template.Id;
        if (!entry.InstallPathEdited)
        {
            if (entry.Suggestion is { } suggestion && suggestion.TemplateId == template.Id)
            {
                entry.InstallPath = suggestion.InstallPath;
                entry.ArchiveRoot = suggestion.ArchiveRoot;
            }
            else if (template.Id != ModTemplates.Custom)
            {
                entry.InstallPath = template.DefaultInstallPath;
            }
        }

        await OnEntriesChanged.InvokeAsync();
    }

    private async Task SetInstallPath(FileEntry entry, string? value)
    {
        entry.InstallPath = value ?? string.Empty;
        entry.InstallPathEdited = true;
        await OnEntriesChanged.InvokeAsync();
    }

    private async Task SetArchiveRoot(FileEntry entry, string? value)
    {
        entry.ArchiveRoot = value?.Trim() ?? string.Empty;
        entry.InstallPathEdited = true;
        await OnEntriesChanged.InvokeAsync();
    }

    private static string TemplateName(string? id) => ModTemplates.Find(id)?.Name ?? id ?? string.Empty;

    // ── Remove ────────────────────────────────────────────────────────────────

    private async Task Remove(int index)
    {
        if (index >= 0 && index < Entries.Count)
        {
            Entries.RemoveAt(index);
            await OnEntriesChanged.InvokeAsync();
        }
    }

    // ── Folder-select dialog ──────────────────────────────────────────────────

    private void OpenFsd(int index)
    {
        _fsdTargetIndex = index;
        _fsdOpen = true;
    }

    private async Task ApplyFsdPath(string path)
    {
        if (_fsdTargetIndex >= 0 && _fsdTargetIndex < Entries.Count)
            await SetInstallPath(Entries[_fsdTargetIndex], path);
    }
}
