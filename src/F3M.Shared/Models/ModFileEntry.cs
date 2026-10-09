namespace F3M.Shared.Models;

/// <summary>What kind of file a placement is. Serialised lower-case in the catalog.</summary>
public enum PlacementKind
{
    /// <summary>A .dll under BepInEx/plugins.</summary>
    Plugin,

    /// <summary>A .cfg under BepInEx/config.</summary>
    Config,

    Other,
}

/// <summary>
/// One file that ends up in the game folder: where it comes from inside the upload, where it goes, and its hash.
/// A plain upload has one entry. An archive has one entry per file it contains.
/// </summary>
public class ModFileEntry
{
    public int Id { get; set; }
    public int ModFileId { get; set; }

    /// <summary>Path inside the upload: the archive entry, or the file name for a plain file.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Game-relative target, '/' separated.</summary>
    public string To { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>SHA-256 of the content, lower-case hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public PlacementKind Kind { get; set; } = PlacementKind.Other;
}
