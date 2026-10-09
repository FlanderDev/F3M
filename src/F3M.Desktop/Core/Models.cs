using F3M.Shared.Models;

namespace F3M.Desktop.Core;

/// <summary>User settings, stored in settings.json. Paths are absolute; the server URL has no trailing slash.</summary>
public sealed class AppSettings
{
    public string ServerUrl { get; set; } = F3M.Shared.Configuration.PublicSiteUrl;
    public string GameFolder { get; set; } = string.Empty;

    /// <summary>Game executable, relative to <see cref="GameFolder"/>. Empty until the user picks it.</summary>
    public string GameExecutable { get; set; } = string.Empty;

    /// <summary>When set, Play launches through steam://rungameid/ instead of starting the executable.</summary>
    public string SteamAppId { get; set; } = string.Empty;

    public bool DeployBeforePlay { get; set; } = true;
    public int UpdateCheckHours { get; set; } = 6;
    public int CacheLimitGb { get; set; } = 5;
    public bool StartAtLogin { get; set; }
}

/// <summary>A local profile: a named set of mod groups, with optional local version pins. Never sent to the server.</summary>
public sealed class ProfileDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profile";
    public List<int> GroupIds { get; set; } = [];

    /// <summary>groupId to versionId. Local only; pins are never shared.</summary>
    public Dictionary<int, int> Pins { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What is currently in the game folder, per game (plan 6.7).</summary>
public sealed class DeployedState
{
    public string GamePath { get; set; } = string.Empty;
    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public List<DeployedMod> Mods { get; set; } = [];
}

public sealed class DeployedMod
{
    public int GroupId { get; set; }
    public int VersionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset DeployedAt { get; set; }
    public List<DeployedFile> Files { get; set; } = [];
    public List<GeneratedPathRef> GeneratedPaths { get; set; } = [];

    /// <summary>Directories this deploy created, removed again when the mod leaves if they are empty.</summary>
    public List<string> CreatedDirectories { get; set; } = [];
}

public sealed class DeployedFile
{
    /// <summary>Game-relative, forward slashes.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Hash of the file as it is in the game folder after the last deploy (or the user's version, if kept).</summary>
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Kind { get; set; } = "other";
    public bool CreatedByMod { get; set; } = true;

    /// <summary>Hash of the file as the mod shipped it. Differs from Sha256 when the user changed the file.</summary>
    public string DefaultSha256 { get; set; } = string.Empty;
}

/// <summary>First line of the journal file: identifies one deploy.</summary>
public sealed class JournalHeader
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>One journal line per game-folder change. Paths are absolute so a rollback does not depend on settings.</summary>
public sealed class JournalOp
{
    /// <summary>"copy", "delete" or "mkdir".</summary>
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>True when the path existed before the operation. A rollback restores <see cref="Backup"/> in that case.</summary>
    public bool Existed { get; set; }
    public string? Backup { get; set; }
}
