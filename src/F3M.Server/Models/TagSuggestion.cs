using F3M.Shared.Models;

namespace F3M.Server.Models;

/// <summary>
/// A tag users suggest for a mod, or with <see cref="TagId"/> null a request for a tag that doesn't exist yet. There is
/// at most one per mod and tag, in any state: a rejected one keeps the tag from being suggested for that mod again.
/// </summary>
public class TagSuggestion
{
    public int Id { get; set; }
    public int ModGroupId { get; set; }

    /// <summary>Null while it is a request: <see cref="RequestedName"/> names the tag to create.</summary>
    public int? TagId { get; set; }

    /// <summary>A request's tag name (cleaned), kept after the tag was created.</summary>
    public string? RequestedName { get; set; }

    /// <summary>TagRules.Normalize of <see cref="RequestedName"/>, to find open requests for the same name.</summary>
    public string? RequestedNormalizedName { get; set; }

    public string? Reason { get; set; }

    /// <summary>Null once that account was deleted.</summary>
    public int? SuggestedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TagSuggestionState State { get; set; }
    public int? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }

    /// <summary>Upvotes minus downvotes, kept up to date with <see cref="Votes"/> so filters can compare it directly.</summary>
    public int Score { get; set; }

    public List<TagVote> Votes { get; set; } = [];
}

/// <summary>One account's vote on a suggestion.</summary>
public class TagVote
{
    public int SuggestionId { get; set; }
    public int UserId { get; set; }

    /// <summary>1 for, -1 against.</summary>
    public int Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A setting admins change on the site (see SiteSettings), by name.</summary>
public class SiteSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
