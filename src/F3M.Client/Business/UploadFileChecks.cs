using F3M.Client.Models;
using F3M.Shared.Helpers;

namespace F3M.Client.Business;

/// <summary>
/// What would make the server reject a file of the upload form, checked before it is sent: the same rules as
/// <see cref="InstallPaths"/> and the server's archive-folder checks. Type rules are only warnings and live in
/// <see cref="FileEntry.TypeWarningsWith"/>.
/// </summary>
public static class UploadFileChecks
{
    /// <summary>Null when the file's install path and archive folder are fine.</summary>
    public static string? Problem(FileEntry entry)
    {
        if (!InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out var placement))
            return "Not a valid install path. Use a folder inside the game, like BepInEx/plugins/MyMod (no “..”, drive letters or special characters).";

        if (entry.ArchiveRoot.Length == 0)
            return null;

        if (!placement.IsArchive)
            return "Only archives have an archive folder; clear it for this file.";

        if (!InstallPaths.TryNormalize(entry.ArchiveRoot, out _))
            return $"“{entry.ArchiveRoot}” is not a valid archive folder.";

        // An unreadable archive is reported by the preview; the server reads it again either way.
        if (entry.Listing is { Error: null } && entry.Targets is { Count: 0 })
            return $"The archive has no files inside “{entry.ArchiveRoot}”.";

        return null;
    }

    public static bool HasProblems(IEnumerable<FileEntry> entries) => entries.Any(e => Problem(e) is not null);
}
