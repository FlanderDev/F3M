namespace F3M.Shared.Helpers;

/// <summary>Where one uploaded file ends up: a single file at <see cref="Target"/>, or an archive extracted into it.</summary>
/// <param name="IsArchive">True for .zip/.7z/.rar, which are extracted rather than copied.</param>
/// <param name="Target">Game-relative, '/' separated. A file path for plain files; the folder to extract into for archives ("" = game folder).</param>
public sealed record Placement(bool IsArchive, string Target);

/// <summary>
/// The rules that turn "file + install path" into a place in the game folder.
/// One implementation shared by the upload preview and server-side upload validation, so the author
/// sees the same install-path rules that are stored for players.
/// </summary>
public static class InstallPaths
{
    public const string DefaultPluginDirectory = "BepInEx/plugins";

    /// <summary>Matches the length of the database column.</summary>
    public const int MaxLength = 260;

    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsArchive(string fileName) =>
        ArchiveExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a relative path into segments. "." is dropped; ".." , drive letters / stream markers,
    /// characters Windows rejects, control characters, trailing dots or spaces and device names are not allowed.
    /// Both slash styles are accepted. Empty input is valid and yields no segments.
    /// </summary>
    public static bool TrySplit(string? input, out List<string> parts, out string? invalidSegment)
    {
        parts = [];
        invalidSegment = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;

        foreach (var segment in input.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == ".."
                || segment.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(segment)))
            {
                invalidSegment = segment;
                return false;
            }

            parts.Add(segment);
        }

        return true;
    }

    /// <summary>Validates and normalises an install path to '/' separators. Empty is valid.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (!TrySplit(input, out var parts, out _))
            return false;

        var joined = string.Join('/', parts);
        if (joined.Length > MaxLength)
            return false;

        normalized = joined;
        return true;
    }

    /// <summary>
    /// The author's install path is the full target path including the file name (the upload dialog
    /// pre-fills the name); a path whose last part isn't the file's name is taken as a folder.
    /// No path: plain files go to BepInEx/plugins, archives are extracted into the game folder.
    /// An archive's "file path" means the folder it would have been saved in.
    /// </summary>
    public static bool TryPlan(string originalName, string? installPath, out Placement placement)
    {
        placement = new Placement(false, string.Empty);

        var name = Path.GetFileName((originalName ?? string.Empty).Replace('\\', '/'));
        if (!TrySplit(name, out var nameParts, out _) || nameParts.Count != 1)
            return false;
        name = nameParts[0];

        if (!TrySplit(installPath, out var parts, out _))
            return false;

        var archive = IsArchive(name);
        string target;
        if (parts.Count == 0)
            target = archive ? string.Empty : $"{DefaultPluginDirectory}/{name}";
        else if (parts[^1].Equals(name, StringComparison.OrdinalIgnoreCase))
            target = string.Join('/', archive ? parts.Take(parts.Count - 1) : parts);
        else
            target = archive ? string.Join('/', parts) : $"{string.Join('/', parts)}/{name}";

        if (target.Length > MaxLength)
            return false;

        placement = new Placement(archive, target);
        return true;
    }

    /// <summary>The game-relative path of one archive entry once the archive is extracted into <paramref name="extractDirectory"/>.</summary>
    public static bool TryExpand(string extractDirectory, string entryPath, out string relative)
    {
        relative = string.Empty;
        if (!TrySplit(extractDirectory, out var baseParts, out _)
            || !TrySplit(entryPath, out var entryParts, out _)
            || entryParts.Count == 0)
        {
            return false;
        }

        relative = string.Join('/', baseParts.Concat(entryParts));
        return true;
    }
}
