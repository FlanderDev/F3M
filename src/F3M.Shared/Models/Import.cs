namespace F3M.Shared.Models;

/// <summary>What the F95zone import (admin page) is doing right now.</summary>
public enum ImportJobKind
{
    None,
    Download,
    Import,

    /// <summary>Download and import of the frameworks only (setup page).</summary>
    Frameworks,
}

/// <summary>Where one mod of the import plan stands.</summary>
public enum ImportModState
{
    /// <summary>Some of its files are not on the server yet.</summary>
    NotDownloaded,

    /// <summary>A file could not be downloaded, or an archive lacks the folder the plan names.</summary>
    DownloadFailed,

    /// <summary>All files are on the server; nothing is imported yet.</summary>
    Downloaded,

    /// <summary>Some versions are imported, others not yet.</summary>
    PartlyImported,

    /// <summary>Every version of the plan is on the site.</summary>
    Imported,

    /// <summary>The server found warnings about the files' types; an admin has to accept them.</summary>
    HeldBack,

    /// <summary>The import of a version was rejected.</summary>
    Failed,
}

/// <summary>Everything the F95zone import page shows.</summary>
public class ImportStatusDto
{
    public ImportPlanInfoDto Plan { get; set; } = new();
    public ImportJobDto Job { get; set; } = new();
    public List<ImportModDto> Mods { get; set; } = [];
    public List<ImportSkippedDto> Skipped { get; set; } = [];

    /// <summary>Newest last.</summary>
    public List<ImportLogLineDto> Log { get; set; } = [];
}

/// <summary>The import plan the server works from: its own copy of the JSON shipped with it, or one an admin uploaded.</summary>
public class ImportPlanInfoDto
{
    /// <summary>True while the server uses the plan shipped with this build.</summary>
    public bool IsBundled { get; set; }

    /// <summary>When an admin uploaded the plan in use; null for the shipped one.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Why the plan can't be used; the page then offers only replacing or resetting it.</summary>
    public string? Error { get; set; }

    public int Mods { get; set; }
    public int Versions { get; set; }
    public int Files { get; set; }
    public int FilesDownloaded { get; set; }

    /// <summary>Size of the downloaded files on the server.</summary>
    public long DownloadedBytes { get; set; }
}

/// <summary>The running (or last) step of the import.</summary>
public class ImportJobDto
{
    public ImportJobKind Kind { get; set; }
    public bool Running { get; set; }
    public bool Cancelling { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }

    /// <summary>What is being worked on right now, e.g. a file name.</summary>
    public string? Current { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>One line about the outcome, once the step has finished.</summary>
    public string? Summary { get; set; }
}

/// <summary>One mod of the import plan.</summary>
public class ImportModDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public ImportModState State { get; set; }

    /// <summary>The newest imported version, for a link to the mod page; null until something is imported.</summary>
    public int? ModId { get; set; }

    public int Versions { get; set; }
    public int VersionsImported { get; set; }
    public int Files { get; set; }
    public int FilesDownloaded { get; set; }

    /// <summary>What went wrong last, for <see cref="ImportModState.DownloadFailed"/> and <see cref="ImportModState.Failed"/>.</summary>
    public string? Issue { get; set; }

    /// <summary>For <see cref="ImportModState.HeldBack"/>: the version that was held back, and why.</summary>
    public string? HeldBackVersion { get; set; }
    public List<string> Warnings { get; set; } = [];

    /// <summary>Keys of the mods this one needs.</summary>
    public List<string> Dependencies { get; set; } = [];

    /// <summary>The plan's note: what was left out, and why.</summary>
    public string? Note { get; set; }
}

/// <summary>A mod of the community list that the plan leaves out, with the reason.</summary>
public class ImportSkippedDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class ImportLogLineDto
{
    public DateTime At { get; set; }
    public bool IsError { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Starts the import step.</summary>
public class ImportStartDto
{
    /// <summary>Only these mods (by key); null for every mod that is not fully imported.</summary>
    public List<string>? OnlyKeys { get; set; }

    /// <summary>Mods whose warnings the admin accepted: they are stored despite them.</summary>
    public List<string> AcceptWarningsFor { get; set; } = [];
}

/// <summary>An import plan as the JSON text of the file.</summary>
public class ImportPlanJsonDto
{
    public string Json { get; set; } = string.Empty;
}
