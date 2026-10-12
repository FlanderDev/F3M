namespace F3M.Client.Models;

/// <summary>One file or folder of the "where will the files end up" preview.</summary>
public sealed class PreviewNode
{
    public required string Name { get; init; }

    /// <summary>Game-relative path, '/' separated; empty for the game folder itself.</summary>
    public string Path { get; init; } = string.Empty;

    public bool IsFile { get; init; }

    public long Size { get; set; }

    /// <summary>The uploaded file that puts this here.</summary>
    public string? Source { get; set; }

    /// <summary>Two uploaded files (or a file and a folder) want the same spot.</summary>
    public bool Conflict { get; set; }

    /// <summary>Files below this node (folders only).</summary>
    public int FileCount { get; set; }

    public SortedDictionary<string, PreviewNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Optional groups (set by InstallPreview whenever the groups change) ──

    /// <summary>The optional group of a file, or of a folder whose files are all in that one group; null otherwise.</summary>
    public string? OptionalGroup { get; set; }

    /// <summary>Folders: some files below are optional, but not all of them in one group.</summary>
    public bool OptionalMixed { get; set; }

    /// <summary>Optional files below this node (folders), or 1 for an optional file.</summary>
    public int OptionalCount { get; set; }
}

public sealed record PreviewResult(PreviewNode Root, int FileCount, long TotalBytes, IReadOnlyList<string> Warnings);
