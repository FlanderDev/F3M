using F3M.Shared.Helpers;
using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

/// <summary>A tag as the pages see it, with which tags include it.</summary>
public sealed class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// For the built-in tag of a mod type: its <see cref="Helpers.ModTemplates"/> id. Mods get such a tag from their
    /// files' types, never by hand; it can't be deleted, merged or include other tags. Null for ordinary tags.
    /// </summary>
    public string? TypeId { get; set; }

    public bool IsType => TypeId is not null;

    /// <summary>The tags that include this one directly: it is a kind of each of them.</summary>
    public List<int> ParentIds { get; set; } = [];

    /// <summary>Mods tagged with exactly this tag.</summary>
    public int ModCount { get; set; }

    /// <summary>Mods a filter by this tag finds: tagged with it or with any tag it includes.</summary>
    public int TotalModCount { get; set; }
}

/// <summary>Admin: a new tag, or the new state of one. The inclusions replace the current ones.</summary>
public sealed class TagEditDto
{
    [Required, MaxLength(TagRules.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(TagRules.MaxDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>The tags this one includes (they are kinds of it).</summary>
    public List<int> IncludesIds { get; set; } = [];

    /// <summary>The tags that include this one (it is a kind of them).</summary>
    public List<int> IncludedInIds { get; set; } = [];
}
