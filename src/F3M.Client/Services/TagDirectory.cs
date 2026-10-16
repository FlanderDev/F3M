using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Client.Services;

/// <summary>
/// The site's tags, loaded once per visit and shared by every page and card that shows or picks tags. The admin tag
/// page calls <see cref="ReloadAsync"/> after each change.
/// </summary>
public sealed class TagDirectory(ITagsApi api)
{
    private Task<List<TagDto>>? _loading;
    private Dictionary<int, TagDto> _byId = [];

    public IReadOnlyList<TagDto> Tags { get; private set; } = [];
    public TagGraph Graph { get; private set; } = TagGraph.FromTags([]);

    /// <summary>Raised after the tags were (re)loaded, so cards already showing can fill in the names.</summary>
    public event Action? Changed;

    /// <summary>Loads the tags unless they are loaded already. A failure leaves the list empty; tags simply don't show.</summary>
    public async Task LoadAsync()
    {
        _loading ??= api.GetTags();
        try
        {
            Apply(await _loading);
        }
        catch
        {
            _loading = null;
        }
    }

    public async Task ReloadAsync()
    {
        _loading = null;
        await LoadAsync();
    }

    private void Apply(List<TagDto> tags)
    {
        if (ReferenceEquals(tags, Tags))
            return;
        Tags = tags;
        _byId = tags.ToDictionary(t => t.Id);
        Graph = TagGraph.FromTags(tags);
        Changed?.Invoke();
    }

    public TagDto? Find(int id) => _byId.GetValueOrDefault(id);

    public TagDto? Find(string name)
    {
        var normalized = TagRules.Normalize(name);
        return Tags.FirstOrDefault(t => TagRules.Normalize(t.Name) == normalized);
    }

    /// <summary>The tags among <paramref name="ids"/> that exist, by name.</summary>
    public List<TagDto> Resolve(IEnumerable<int> ids) =>
        [.. ids.Select(Find).OfType<TagDto>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The names of the tags that include this one, directly or through others; for a tooltip.</summary>
    public List<string> AncestorNames(int id) =>
        [.. Graph.SelfAndAncestors(id).Where(a => a != id).Select(Find).OfType<TagDto>().Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase)];
}
