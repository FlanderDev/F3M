using F3M.Shared.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

// ── ModGroup ─────────────────────────────────────────────────────────────────
// Represents a logical mod (all versions share one group).
// The "card" shown in browse is always the latest version's data.

public class ModGroup
{
    /// <summary><see cref="OwnerId"/> of a mod imported from F95zone whose uploader has not signed in yet.</summary>
    public const int UnclaimedOwnerId = -1;

    public int Id { get; set; }
    public int OwnerId { get; set; }   // UserId who created this group
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// For imported mods: the F95zone user id of whoever posted the files. When that user signs in through F95,
    /// the mod becomes theirs. Ownership never follows names, which F95 users can change.
    /// </summary>
    [MaxLength(30)]
    public string? F95OwnerUserId { get; set; }

    /// <summary>The F95zone name of <see cref="F95OwnerUserId"/> at import time, for display only.</summary>
    [MaxLength(50)]
    public string? F95OwnerName { get; set; }

    /// <summary>Where the mod was originally published, e.g. its F95zone post.</summary>
    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    /// <summary>When the F95 uploader took over an imported mod; null while unclaimed or for mods uploaded here.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Imported, and its uploader has not taken it over yet.</summary>
    public bool IsUnclaimed => OwnerId == UnclaimedOwnerId;
}

// ── Mod (version record) ──────────────────────────────────────────────────────
public class Mod : IId, IName
{
    public int Id { get; set; }

    // Link to the logical mod
    public int ModGroupId { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(Configuration.ModDescriptionMaxSize)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(80)]
    public string Author { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Use this instead of Version for faster filtering. Ensure this is true for only on entry per <see cref="ModGroupId"/>!</summary>
    public bool IsLatestVersion { get; set; } = false;

    [MaxLength(50)]
    public string Category { get; set; } = "General";

    public string? PreviewImageName { get; set; }

    public int DownloadCount { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public bool IsApproved { get; set; } = true;

    public int? UserId { get; set; }

    // Navigation
    public List<ModFile> Files { get; set; } = [];

    /// <summary>Patterns for files this version creates at runtime (plan 5.8).</summary>
    public List<ModGeneratedPath> GeneratedPaths { get; set; } = [];

    /// <summary>
    /// ModGroups (logical mods) that this version depends on — persisted. Points at the group,
    /// not a specific version, so a dependency always follows that mod's current latest version
    /// rather than staying pinned to whatever was newest at upload time.
    /// </summary>
    public List<ModGroup> DependencyGroups { get; set; } = [];

    /// <summary>
    /// Resolved view of <see cref="DependencyGroups"/> for display — each entry is the current
    /// latest approved Mod (version) for a dependency group. NOT an EF-mapped relationship (see
    /// AppDbContext.Ignore); populated by ModsService.ResolveDependenciesAsync at read time.
    /// Empty unless the caller specifically asked for it to be resolved.
    /// </summary>
    public List<Mod> Dependencies { get; set; } = [];
}

// ── ModFile ───────────────────────────────────────────────────────────────────
// One physical file belonging to a mod version.
// A version may contain multiple files (e.g. core + optional DLC packs).
public class ModFile
{
    public int Id { get; set; }
    public int ModId { get; set; }       // → Mod.Id (version)

    [Required]
    public string FileName { get; set; } = string.Empty;   // server-side GUID name
    public string OriginalName { get; set; } = string.Empty;  // original filename shown to user

    [MaxLength(260)]
    public string InstallPath { get; set; } = string.Empty;   // suggested install path

    /// <summary>SHA-256 of the uploaded file (lower-case hex), computed on upload. Empty for files uploaded before catalog support.</summary>
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>True for archives, which are extracted into <see cref="TargetPath"/>; false for plain files.</summary>
    public bool IsArchive { get; set; }

    /// <summary>Game-relative target: the file's own path for plain files, the folder for archives ('/' separated).</summary>
    [MaxLength(260)]
    public string TargetPath { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

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

    [Required, MaxLength(Configuration.ModDescriptionMaxSize)]
    public string Description { get; set; } = string.Empty;

    /// <summary>ModGroup IDs (logical mods) this upload depends on.</summary>
    public List<int> DependencyGroupIds { get; set; } = [];

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category { get; set; } = "Unspecified";

    /// <summary>If set, this upload is a new version of an existing mod group.</summary>
    public int? ModGroupId { get; set; }

    /// <summary>Files the mod creates at runtime that are not part of the archive (plan 5.8).</summary>
    public List<GeneratedPathDto> GeneratedPaths { get; set; } = [];
}

/// <summary>One entry in the multi-file list on the upload form.</summary>
public class FileEntryDto
{
    public string OriginalName { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public byte[] Bytes { get; set; } = [];
}

public class ModListResult
{
    public List<Mod> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

/// <summary>Outcome of claiming an imported mod. <see cref="Message"/> is shown to the user either way.</summary>
public class ClaimModResult
{
    public ClaimModResult() { }
    public ClaimModResult(bool success, string message) => (Success, Message) = (success, message);

    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Admin: who a mod group should belong to. Null <see cref="UserId"/> makes it unclaimed again.</summary>
public class AssignModOwnerDto
{
    public int? UserId { get; set; }
}

/// <summary>All versions for a group, returned by the detail endpoint.</summary>
public class ModVersionsResult
{
    public ModGroup Group { get; set; } = new();
    public List<Mod> Versions { get; set; } = [];
}
