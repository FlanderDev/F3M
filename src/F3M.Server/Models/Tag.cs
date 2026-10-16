namespace F3M.Server.Models;

/// <summary>A tag admins manage; mods can have several. See <see cref="TagInclusion"/> for how tags group each other.</summary>
public class Tag
{
    public int Id { get; set; }

    /// <summary>As shown, cleaned by TagRules.Clean.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>TagRules.Normalize of <see cref="Name"/>; unique, so no two tags differ only in case or spacing.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>"<see cref="ParentId"/> includes <see cref="ChildId"/>": the child is a kind of the parent.</summary>
public class TagInclusion
{
    public int ParentId { get; set; }
    public int ChildId { get; set; }
}

/// <summary>A tag on a mod. Tags belong to the mod (its group), not to one version.</summary>
public class ModGroupTag
{
    public int ModGroupId { get; set; }
    public int TagId { get; set; }
}
