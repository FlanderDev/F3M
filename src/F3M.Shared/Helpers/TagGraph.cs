using F3M.Shared.Models;

namespace F3M.Shared.Helpers;

/// <summary>Limits and name rules for tags, checked by the server and shown by the pages.</summary>
public static class TagRules
{
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 200;

    /// <summary>A mod can have at most this many tags.</summary>
    public const int MaxTagsPerMod = 10;

    /// <summary>The longest chain of tags including each other, counting both ends ("Clothing → Underwear → Lingerie" is 3).</summary>
    public const int MaxDepth = 6;

    /// <summary>
    /// Separates tags in the browse page's address (?tags=a,b), so a name can't contain it. The other characters names
    /// can't contain would break that address or look like something else.
    /// </summary>
    public const char ListSeparator = ',';

    private static readonly char[] ForbiddenCharacters = [ListSeparator, ';', '/', '\\', '?', '#', '%', '<', '>', '"'];

    /// <summary>The name with spaces trimmed and runs of spaces made one, the way it is stored.</summary>
    public static string Clean(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The form names are compared in: two names that differ only in case or spacing are the same tag.</summary>
    public static string Normalize(string? name) => Clean(name).ToUpperInvariant();

    /// <summary>Why <paramref name="name"/> (already cleaned) can't be a tag's name; null when it can.</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length == 0)
            return "A tag needs a name.";
        if (name.Length > MaxNameLength)
            return $"A tag's name can be at most {MaxNameLength} characters long.";
        if (name.Any(char.IsControl))
            return "A tag's name can't contain control characters.";
        if (name.IndexOfAny(ForbiddenCharacters) >= 0)
            return $"A tag's name can't contain any of these: {string.Join(' ', ForbiddenCharacters)}";
        return null;
    }
}

/// <summary>
/// The tags and which ones include which, as a graph: "A includes B" means B is a kind of A, so whatever is tagged B
/// also counts as A. A tag can be included by several others, but never (through others) by itself. Used by the server
/// to check changes and to filter, and by the pages to offer only changes the server accepts.
/// </summary>
public sealed class TagGraph
{
    private readonly Dictionary<int, HashSet<int>> _children = [];
    private readonly Dictionary<int, HashSet<int>> _parents = [];

    /// <param name="tagIds">Every tag.</param>
    /// <param name="inclusions">Every inclusion; ones naming an unknown tag are left out.</param>
    public TagGraph(IEnumerable<int> tagIds, IEnumerable<(int ParentId, int ChildId)> inclusions)
    {
        foreach (var id in tagIds)
        {
            _children.TryAdd(id, []);
            _parents.TryAdd(id, []);
        }

        foreach (var (parent, child) in inclusions)
        {
            if (_children.TryGetValue(parent, out var children) && _parents.TryGetValue(child, out var parents))
            {
                children.Add(child);
                parents.Add(parent);
            }
        }
    }

    public static TagGraph FromTags(IEnumerable<TagDto> tags)
    {
        var list = tags.ToList();
        return new TagGraph(list.Select(t => t.Id), list.SelectMany(t => t.ParentIds.Select(p => (p, t.Id))));
    }

    public IEnumerable<int> TagIds => _children.Keys;

    public bool Contains(int id) => _children.ContainsKey(id);

    public IReadOnlyCollection<int> ChildrenOf(int id) => _children.TryGetValue(id, out var set) ? set : [];

    public IReadOnlyCollection<int> ParentsOf(int id) => _parents.TryGetValue(id, out var set) ? set : [];

    /// <summary>The tag and every tag it includes, directly or through others: what a filter by it finds.</summary>
    public HashSet<int> SelfAndDescendants(int id) => Walk(id, _children);

    /// <summary>The tag and every tag that includes it, directly or through others.</summary>
    public HashSet<int> SelfAndAncestors(int id) => Walk(id, _parents);

    private static HashSet<int> Walk(int start, Dictionary<int, HashSet<int>> edges)
    {
        var seen = new HashSet<int>();
        if (!edges.ContainsKey(start))
            return seen;

        var todo = new Stack<int>();
        todo.Push(start);
        while (todo.Count > 0)
        {
            var id = todo.Pop();
            if (!seen.Add(id))
                continue;
            foreach (var next in edges[id])
                todo.Push(next);
        }
        return seen;
    }

    /// <summary>
    /// The graph with the inclusions of <paramref name="tagId"/> replaced: what it includes, and what it is included
    /// in. Adds the tag when it is new. Doesn't check the result; see <see cref="Problem"/>.
    /// </summary>
    public TagGraph WithInclusions(int tagId, IEnumerable<int> includes, IEnumerable<int> includedIn)
    {
        var edges = Edges().Where(e => e.ParentId != tagId && e.ChildId != tagId)
            .Concat(includes.Select(c => (tagId, c)))
            .Concat(includedIn.Select(p => (p, tagId)));
        return new TagGraph(TagIds.Append(tagId), edges);
    }

    public IEnumerable<(int ParentId, int ChildId)> Edges() =>
        _children.SelectMany(pair => pair.Value.Select(child => (pair.Key, child)));

    /// <summary>
    /// What is wrong with the graph, null when nothing is: a tag including itself (directly or through others), or a
    /// chain longer than <see cref="TagRules.MaxDepth"/>. <paramref name="name"/> turns ids into names for the message.
    /// </summary>
    public string? Problem(Func<int, string> name)
    {
        // Longest chain ending at each tag, worked out from the top (Kahn's algorithm). Tags that never become free
        // to visit sit on a loop.
        var waiting = _parents.ToDictionary(p => p.Key, p => p.Value.Count);
        var depth = _children.Keys.ToDictionary(id => id, _ => 1);
        var free = new Queue<int>(waiting.Where(w => w.Value == 0).Select(w => w.Key));
        var visited = 0;

        while (free.Count > 0)
        {
            var id = free.Dequeue();
            visited++;
            foreach (var child in _children[id])
            {
                depth[child] = Math.Max(depth[child], depth[id] + 1);
                if (--waiting[child] == 0)
                    free.Enqueue(child);
            }
        }

        if (visited < _children.Count)
        {
            var loop = FindLoop(waiting.Where(w => w.Value > 0).Select(w => w.Key).ToHashSet());
            return $"That would make a loop: {string.Join(" → ", loop.Select(name))}. A tag can't include itself, not even through other tags.";
        }

        var deepest = depth.OrderByDescending(d => d.Value).FirstOrDefault();
        if (deepest.Value > TagRules.MaxDepth)
            return $"That would make a chain of {deepest.Value} tags ending at {name(deepest.Key)}; at most {TagRules.MaxDepth} can include each other in a row.";

        return null;
    }

    /// <summary>One loop among <paramref name="candidates"/> (the tags Kahn's algorithm couldn't free), first tag repeated at the end.</summary>
    private List<int> FindLoop(HashSet<int> candidates)
    {
        // Every candidate has a parent among the candidates, so walking up parents must come back to a tag seen before.
        var path = new List<int>();
        var position = new Dictionary<int, int>();
        var id = candidates.Min();
        while (!position.ContainsKey(id))
        {
            position[id] = path.Count;
            path.Add(id);
            id = _parents[id].Where(candidates.Contains).Min();
        }

        // The walk went up from child to parent; turn it around so it reads "includes", starting at the lowest id so
        // the same loop is always told the same way.
        var loop = path.Skip(position[id]).Reverse().ToList();
        var start = loop.IndexOf(loop.Min());
        loop = [.. loop.Skip(start), .. loop.Take(start)];
        loop.Add(loop[0]);
        return loop;
    }

    /// <summary>
    /// Whether "<paramref name="parentId"/> includes <paramref name="childId"/>" already follows through other tags,
    /// so adding it directly changes nothing. Returns the tag it goes through, for the warning.
    /// </summary>
    public int? ImpliedThrough(int parentId, int childId)
    {
        foreach (var direct in ChildrenOf(parentId))
        {
            if (direct != childId && SelfAndDescendants(direct).Contains(childId))
                return direct;
        }
        return null;
    }
}
