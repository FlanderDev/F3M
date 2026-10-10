namespace F3M.Client.Models;

/// <summary>One file or folder of the "where will the files end up" preview.</summary>
public sealed class PreviewNode
{
    public required string Name { get; init; }

    public bool IsFile { get; init; }

    public long Size { get; set; }

    /// <summary>The uploaded file that puts this here.</summary>
    public string? Source { get; set; }

    /// <summary>Two uploaded files (or a file and a folder) want the same spot.</summary>
    public bool Conflict { get; set; }

    /// <summary>Files below this node (folders only).</summary>
    public int FileCount { get; set; }

    public SortedDictionary<string, PreviewNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PreviewResult(PreviewNode Root, int FileCount, long TotalBytes, IReadOnlyList<string> Warnings);
