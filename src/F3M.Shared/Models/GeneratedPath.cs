using System.ComponentModel.DataAnnotations;
using F3M.Shared.Helpers;

namespace F3M.Shared.Models;

/// <summary>
/// What a file a mod creates at runtime is, and what happens to it when the mod leaves the game (plan 5.8).
/// Serialised lower-case in the catalog: "generated", "cache", "userdata".
/// </summary>
public enum GeneratedPathKind
{
    /// <summary>Created by the mod. Removed when the mod leaves a profile, after the user confirms.</summary>
    Generated,

    /// <summary>Disposable and regenerated automatically. Removed without asking.</summary>
    Cache,

    /// <summary>The user's own data, such as saves. Never removed by F3M.</summary>
    UserData,
}

/// <summary>A glob pattern, relative to the game folder, for files a mod creates at runtime.</summary>
public class ModGeneratedPath
{
    public int Id { get; set; }
    public int ModId { get; set; }

    [Required, MaxLength(GeneratedPathRules.MaxLength)]
    public string Pattern { get; set; } = string.Empty;

    public GeneratedPathKind Kind { get; set; } = GeneratedPathKind.Generated;
}

/// <summary>Upload form entry. Bound from GeneratedPaths[0].Pattern, GeneratedPaths[0].Kind, and so on.</summary>
public class GeneratedPathDto
{
    [Required, MaxLength(GeneratedPathRules.MaxLength)]
    public string Pattern { get; set; } = string.Empty;

    public GeneratedPathKind Kind { get; set; } = GeneratedPathKind.Generated;
}
