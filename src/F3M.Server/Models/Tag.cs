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

    /// <summary>
    /// Set for the built-in tag of a mod type (a ModTemplates id): mods get it from their files' types, never by hand,
    /// and it can't be deleted or merged. Null for the tags admins create.
    /// </summary>
    public string? TypeId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A tag a user created. It works like any tag, and everyone votes on whether it should exist; from enough points
    /// (or when an admin accepts it) it's a regular tag.
    /// </summary>
    public bool IsProposed { get; set; }

    /// <summary>Who created it; null for tags admins made, or once that account was deleted.</summary>
    public int? ProposedById { get; set; }

    /// <summary>Upvotes minus downvotes on the proposal, kept up to date with its votes.</summary>
    public int ProposalScore { get; set; }
}

/// <summary>One account's vote on a proposed tag: 1 for, -1 against.</summary>
public class TagProposalVote
{
    public int TagId { get; set; }
    public int UserId { get; set; }
    public int Value { get; set; }
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
