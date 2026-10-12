using F3M.Shared.Models;

namespace F3M.Client.Models;

/// <summary>An optional group on the upload form, as the author edits it.</summary>
/// <remarks>A class, so edits from the preview and the editor change the same object.</remarks>
public sealed class OptionalGroupDraft
{
    public string Name { get; set; } = string.Empty;

    public List<string> Patterns { get; set; } = [];

    /// <summary>
    /// Suggested by the form (<see cref="F3M.Shared.Helpers.ModTemplates.SuggestOptionalGroups(IReadOnlyCollection{string})"/>)
    /// and not edited since: it is suggested again whenever the files change. Editing it makes it the author's own.
    /// </summary>
    public bool IsSuggestion { get; set; }

    public OptionalGroupDto ToDto() => new() { Name = Name, Patterns = [.. Patterns] };
}
