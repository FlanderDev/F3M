using F3M.Shared.Helpers;
using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public enum TagSuggestionState
{
    Open = 0,

    /// <summary>The tag is on the mod now (or, for a request, was created and suggested).</summary>
    Approved = 1,

    /// <summary>Hidden; the same tag can't be suggested for that mod again.</summary>
    Rejected = 2
}

/// <summary>
/// A tag users suggest for a mod, or (with <see cref="TagId"/> null) a tag they ask an admin to create for it. Visitors
/// vote on it; from <see cref="TagSuggestionLimits.Threshold"/> on, an open suggestion counts in filters and searches
/// like the mod's own tags, until the mod's owner or an admin approves or rejects it.
/// </summary>
public class TagSuggestionDto
{
    public int Id { get; set; }
    public int ModGroupId { get; set; }

    /// <summary>The suggested tag; null for a request for a new tag (<see cref="RequestedName"/>).</summary>
    public int? TagId { get; set; }

    public string? RequestedName { get; set; }

    public string? Reason { get; set; }

    public TagSuggestionState State { get; set; }

    /// <summary>Upvotes minus downvotes.</summary>
    public int Score { get; set; }
    public int Up { get; set; }
    public int Down { get; set; }

    /// <summary>The signed-in visitor's vote: 1, -1 or 0 for none.</summary>
    public int MyVote { get; set; }

    /// <summary>Open, a tag, and its score reached the limit: it counts in filters and searches.</summary>
    public bool Counts { get; set; }

    public string? SuggestedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsRequest => TagId is null;
}

/// <summary>A suggestion as the admin queue shows it: with its mod and who voted.</summary>
public sealed class TagSuggestionAdminDto : TagSuggestionDto
{
    public string ModName { get; set; } = string.Empty;

    /// <summary>The mod's latest version, for the link to its page.</summary>
    public int ModId { get; set; }

    public List<string> UpVoters { get; set; } = [];
    public List<string> DownVoters { get; set; } = [];

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

/// <summary>The suggestions on a mod's page, and what the visitor may do with them.</summary>
public sealed class ModTagSuggestions
{
    /// <summary>Open suggestions and requests, highest score first.</summary>
    public List<TagSuggestionDto> Items { get; set; } = [];

    public int Threshold { get; set; }

    /// <summary>The points a proposed tag needs to be a regular one.</summary>
    public int TagThreshold { get; set; }

    /// <summary>Signed in, and not the mod's owner (owners decide instead of voting).</summary>
    public bool CanVote { get; set; }

    /// <summary>The mod's owner or an admin: may approve and reject.</summary>
    public bool CanDecide { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>Tags rejected for this mod: they can't be suggested again.</summary>
    public List<int> RejectedTagIds { get; set; } = [];

    /// <summary>New suggestions and requests the visitor may still start today; null without a limit (admins).</summary>
    public int? RemainingToday { get; set; }
}

/// <summary>A new suggestion: an existing tag, or a request for a new one by name.</summary>
public sealed class TagSuggestRequest
{
    public int? TagId { get; set; }

    [MaxLength(TagRules.MaxNameLength)]
    public string? NewTagName { get; set; }

    /// <summary>For a new tag: the existing tag it goes under. Optional.</summary>
    public int? ParentTagId { get; set; }

    /// <summary>For a new tag: what it means. Optional.</summary>
    [MaxLength(TagRules.MaxDescriptionLength)]
    public string? NewTagDescription { get; set; }

    [MaxLength(TagSuggestionLimits.MaxReasonLength)]
    public string? Reason { get; set; }
}

/// <summary>A tag a user created, with the votes on whether it should exist.</summary>
public sealed class TagProposalDto
{
    public int TagId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<int> ParentIds { get; set; } = [];

    public int Score { get; set; }
    public int Up { get; set; }
    public int Down { get; set; }

    /// <summary>The signed-in visitor's vote: 1, -1 or 0 for none.</summary>
    public int MyVote { get; set; }

    /// <summary>False once accepted: by votes or by an admin.</summary>
    public bool IsProposed { get; set; }

    public string? ProposedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Mods it's on or suggested for.</summary>
    public int ModCount { get; set; }

    /// <summary>Admins only: who voted.</summary>
    public List<string> UpVoters { get; set; } = [];
    public List<string> DownVoters { get; set; } = [];
}

public sealed class TagVoteRequest
{
    /// <summary>1 for, -1 against, 0 to take the vote back.</summary>
    [Range(-1, 1)]
    public int Value { get; set; }
}

/// <summary>Admin: the settings of the suggestions, changeable on the tags page.</summary>
public sealed class TagSuggestionLimits
{
    public const int DefaultThreshold = 3;
    public const int DefaultDailyLimit = 5;
    public const int DefaultTagThreshold = 5;
    public const int MaxReasonLength = 200;

    /// <summary>The score from which an open suggestion counts in filters and searches.</summary>
    [Range(1, 1000)]
    public int Threshold { get; set; } = DefaultThreshold;

    /// <summary>New suggestions and requests one account may start per 24 hours (admins have no limit).</summary>
    [Range(1, 1000)]
    public int DailyLimit { get; set; } = DefaultDailyLimit;

    /// <summary>The score from which a tag a user created is a regular tag.</summary>
    [Range(1, 1000)]
    public int TagThreshold { get; set; } = DefaultTagThreshold;
}
