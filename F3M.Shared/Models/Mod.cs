using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

// ── ModGroup ─────────────────────────────────────────────────────────────────
// Represents a logical mod (all versions share one group).
// The "card" shown in browse is always the latest version's data.

public class ModGroup
{
    public int    Id       { get; set; }
    public int?   OwnerId  { get; set; }   // UserId who created this group
    public string Author   { get; set; } = string.Empty;
}

// ── Mod (version record) ──────────────────────────────────────────────────────
public class Mod
{
    public int Id { get; set; }

    // Link to the logical mod
    public int ModGroupId { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(80)]
    public string Author { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category { get; set; } = "General";

    public string? PreviewImageName { get; set; }

    public int DownloadCount { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public bool IsApproved { get; set; } = true;

    public int? UserId { get; set; }

    // Navigation
    public List<ModFile> Files { get; set; } = [];
}

// ── ModFile ───────────────────────────────────────────────────────────────────
// One physical file belonging to a mod version.
// A version may contain multiple files (e.g. core + optional DLC packs).
public class ModFile
{
    public int    Id          { get; set; }
    public int    ModId       { get; set; }       // → Mod.Id (version)

    [Required]
    public string FileName    { get; set; } = string.Empty;   // server-side GUID name
    public string OriginalName { get; set; } = string.Empty;  // original filename shown to user

    [MaxLength(260)]
    public string InstallPath { get; set; } = string.Empty;   // suggested install path

    public long   FileSizeBytes { get; set; }

    public string FileSizeDisplay =>
        FileSizeBytes >= 1_048_576
            ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
            : $"{FileSizeBytes / 1024.0:F1} KB";
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class ModUploadDto
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category { get; set; } = "General";

    /// <summary>If set, this upload is a new version of an existing mod group.</summary>
    public int? ModGroupId { get; set; }
}

/// <summary>One entry in the multi-file list on the upload form.</summary>
public class FileEntryDto
{
    public string OriginalName  { get; set; } = string.Empty;
    public string InstallPath   { get; set; } = string.Empty;
    public long   Size          { get; set; }
    public byte[] Bytes         { get; set; } = [];
}

public class ModListResult
{
    public List<Mod> Items      { get; set; } = [];
    public int TotalCount       { get; set; }
    public int Page             { get; set; }
    public int PageSize         { get; set; }
    public int TotalPages       => (int)Math.Ceiling((double)TotalCount / PageSize);
}

/// <summary>All versions for a group, returned by the detail endpoint.</summary>
public class ModVersionsResult
{
    public ModGroup        Group    { get; set; } = new();
    public List<Mod>       Versions { get; set; } = [];
}
