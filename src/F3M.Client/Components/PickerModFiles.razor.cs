using F3M.Client.Models;
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

        Entries.Add(new FileEntry
        {
            OriginalName = f.Name,
            Size = f.Size,
            Bytes = ms.ToArray()
        });

        // Rotate key → fresh <input type=file> DOM node for the next pick.
        _addFileKey++;

        await OnEntriesChanged.InvokeAsync();
    }

    // ── Where will it go ──────────────────────────────────────────────────────

    private static bool IsValidPath(FileEntry entry) =>
        InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out _);

    /// <summary>One line under each file: where it ends up in the game folder.</summary>
    private static string TargetHint(FileEntry entry)
    {
        if (!InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out var placement))
            return "Not a valid install path. Use a folder inside the game, like BepInEx/plugins/MyMod (no “..”, drive letters or special characters).";

        var isDefault = string.IsNullOrWhiteSpace(entry.InstallPath);
        if (placement.IsArchive)
        {
            var folder = placement.Target.Length == 0 ? "the game folder" : placement.Target + "/";
            return $"Will be extracted into {folder}{(isDefault ? " (no path set)" : string.Empty)}";
        }

        return $"Will be installed as {placement.Target}{(isDefault ? " (no path set: default plugin folder)" : string.Empty)}";
    }

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

    private void ApplyFsdPath(string path)
    {
        if (_fsdTargetIndex >= 0 && _fsdTargetIndex < Entries.Count)
            Entries[_fsdTargetIndex].InstallPath = path;
    }
}
