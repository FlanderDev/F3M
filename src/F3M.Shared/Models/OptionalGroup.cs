using System.ComponentModel.DataAnnotations;
using F3M.Shared.Helpers;

namespace F3M.Shared.Models;

/// <summary>
/// A named part of a mod version that the mod does not need to work, such as examples, tutorials or documentation.
/// Its files are matched by game-relative glob patterns (<see cref="OptionalFileRules"/>). The desktop app leaves them
/// out unless the player turns the group on.
/// </summary>
public class ModOptionalGroup
{
    public int Id { get; set; }
    public int ModId { get; set; }

    [Required, MaxLength(OptionalFileRules.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Game-relative patterns, same grammar as generated files ('*', '?', '**').</summary>
    public List<string> Patterns { get; set; } = [];
}

/// <summary>Upload form entry. Bound from OptionalGroups[0].Name, OptionalGroups[0].Patterns[0], and so on.</summary>
public class OptionalGroupDto
{
    [Required, MaxLength(OptionalFileRules.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    public List<string> Patterns { get; set; } = [];
}
