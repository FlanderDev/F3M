using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

/// <summary>
/// Admin page for the tags. The editor checks every change against the whole tree with the same code as the server
/// (<see cref="TagGraph"/>): tags that would make a loop aren't offered, and a chain too long blocks saving. The
/// server checks again, in case another admin changed the tree meanwhile.
/// </summary>
public partial class AdminTags
{
    // Stands for the tag being created, which has no id yet (ids start at 1).
    private const int NewId = 0;

    private bool loading = true;
    private string? loadError;
    private string search = string.Empty;
    private bool treeView;

    private int? editingId;
    private TagEditDto? editing;
    private bool busy;
    private string? saveError;
    private string? notice;

    private List<int> mergeTarget = [];
    private bool confirmMerge;
    private bool confirmDelete;

    private TagDto? Original => editingId is { } id and not NewId ? Directory.Find(id) : null;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            // Fresh counts, not the copy loaded earlier in the visit.
            await Directory.ReloadAsync();
            if (Directory.Tags.Count == 0)
                await Api.GetTags(); // surfaces an error the directory swallowed
        }
        catch (Exception ex)
        {
            loadError = $"The tags could not be loaded: {DescribeError(ex)}";
        }
        finally
        {
            loading = false;
        }
    }

    // ── The list ────────────────────────────────────────────────────────────

    /// <summary>
    /// The rows to show. List: every tag matching the search, by name. Tree: the tags from the top down, each under every
    /// tag that includes it (marked as repeated after the first time); while searching, the matches and the tags above them.
    /// </summary>
    private IEnumerable<(TagDto Tag, int Depth, bool Repeated)> Rows()
    {
        var typed = TagRules.Clean(search);
        bool Matches(TagDto t) => typed.Length == 0 || t.Name.Contains(typed, StringComparison.OrdinalIgnoreCase);

        if (!treeView)
        {
            foreach (var tag in Directory.Tags.Where(Matches))
                yield return (tag, 0, false);
            yield break;
        }

        var graph = Directory.Graph;
        var visible = typed.Length == 0
            ? null
            : Directory.Tags.Where(Matches).SelectMany(t => graph.SelfAndAncestors(t.Id)).ToHashSet();
        var shown = new HashSet<int>();
        var byName = StringComparer.OrdinalIgnoreCase;

        // Depth-first; the tree has no loops and at most TagRules.MaxDepth levels, so this ends.
        var stack = new Stack<(int Id, int Depth)>();
        foreach (var root in Directory.Tags.Where(t => t.ParentIds.Count == 0).OrderByDescending(t => t.Name, byName))
            stack.Push((root.Id, 0));

        while (stack.Count > 0)
        {
            var (id, depth) = stack.Pop();
            if (visible is not null && !visible.Contains(id) || Directory.Find(id) is not { } tag)
                continue;

            var repeated = !shown.Add(id);
            yield return (tag, depth, repeated);
            if (repeated)
                continue; // its children were shown under its first place

            foreach (var child in graph.ChildrenOf(id).Select(Directory.Find).OfType<TagDto>().OrderByDescending(t => t.Name, byName))
                stack.Push((child.Id, depth + 1));
        }
    }

    private string NamesOf(IEnumerable<int> ids) =>
        string.Join(", ", ids.Select(Directory.Find).OfType<TagDto>().Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase));

    // ── The editor ──────────────────────────────────────────────────────────

    private void StartNew() => Open(NewId, new TagEditDto());

    private void Edit(TagDto tag) => Open(tag.Id, new TagEditDto
    {
        Name = tag.Name,
        Description = tag.Description,
        IncludesIds = [.. Directory.Graph.ChildrenOf(tag.Id)],
        IncludedInIds = [.. tag.ParentIds]
    });

    private void Open(int id, TagEditDto dto)
    {
        editingId = id;
        editing = dto;
        saveError = notice = null;
        mergeTarget = [];
        confirmMerge = confirmDelete = false;
    }

    private void Cancel()
    {
        editingId = null;
        editing = null;
        saveError = null;
    }

    /// <summary>The tree as it would be with the edited tag's inclusions.</summary>
    private TagGraph WorkingGraph() =>
        Directory.Graph.WithInclusions(editingId ?? NewId, editing?.IncludesIds ?? [], editing?.IncludedInIds ?? []);

    private string NameIn(int id) => id == editingId ? NameOrPlaceholder : Directory.Find(id)?.Name ?? $"#{id}";

    private string NameOrPlaceholder => TagRules.Clean(editing?.Name) is { Length: > 0 } name ? name : "this tag";

    /// <summary>Why the edited tag can't be saved like this; null when it can.</summary>
    private string? Problem(TagGraph working)
    {
        var name = TagRules.Clean(editing?.Name);
        if (TagRules.NameProblem(name) is { } nameProblem)
            return nameProblem;

        var normalized = TagRules.Normalize(name);
        if (Directory.Tags.FirstOrDefault(t => t.Id != editingId && TagRules.Normalize(t.Name) == normalized) is { } other)
            return $"There already is a tag called \"{other.Name}\".";

        return working.Problem(NameIn);
    }

    /// <summary>
    /// Whether the edited tag could include <paramref name="id"/>: not if that tag is the edited one or above it (a
    /// loop). Inclusions it has already are fine.
    /// </summary>
    private bool CanInclude(TagGraph working, int id) =>
        id != editingId && !working.SelfAndAncestors(editingId ?? NewId).Contains(id);

    /// <summary>Whether <paramref name="id"/> could include the edited tag: not if that tag is the edited one or below it.</summary>
    private bool CanBeIncludedIn(TagGraph working, int id) =>
        id != editingId && !working.SelfAndDescendants(editingId ?? NewId).Contains(id);

    /// <summary>Inclusions that already follow through other tags. Allowed, but they change nothing.</summary>
    private IEnumerable<string> Redundant(TagGraph working)
    {
        var self = editingId ?? NewId;
        foreach (var child in working.ChildrenOf(self))
        {
            if (working.ImpliedThrough(self, child) is { } through)
                yield return $"{NameIn(self)} already includes {NameIn(child)} through {NameIn(through)}, so including it directly changes nothing.";
        }
        foreach (var parent in working.ParentsOf(self))
        {
            if (working.ImpliedThrough(parent, self) is { } through)
                yield return $"{NameIn(parent)} already includes {NameIn(self)} through {NameIn(through)}, so listing it here changes nothing.";
        }
    }

    private List<string> Below(TagGraph working) => Others(working.SelfAndDescendants(editingId ?? NewId));

    private List<string> Above(TagGraph working) => Others(working.SelfAndAncestors(editingId ?? NewId));

    private List<string> Others(IEnumerable<int> ids) =>
        [.. ids.Where(i => i != editingId).Select(NameIn).Order(StringComparer.OrdinalIgnoreCase)];

    private async Task SaveAsync()
    {
        if (editing is null || editingId is not { } id)
            return;

        busy = true;
        saveError = notice = null;
        try
        {
            var saved = id == NewId ? await Api.Create(editing) : await Api.Update(id, editing);
            await Directory.ReloadAsync();
            Edit(Directory.Find(saved.Id) ?? saved);
            notice = id == NewId ? $"Created \"{saved.Name}\"." : $"Saved \"{saved.Name}\".";
        }
        catch (Exception ex)
        {
            saveError = DescribeError(ex);
            // Another admin may have changed the tree; show the current one.
            await Directory.ReloadAsync();
        }
        finally
        {
            busy = false;
        }
    }

    private string MergeSummary(TagDto source, TagDto target)
    {
        var parts = new List<string> { $"{Mods(source.ModCount)} get {target.Name} instead." };
        var children = Directory.Graph.ChildrenOf(source.Id).Where(c => c != target.Id).ToList();
        if (children.Count > 0)
            parts.Add($"{target.Name} will include {NamesOf(children)}.");
        var parents = source.ParentIds.Where(p => p != target.Id).ToList();
        if (parents.Count > 0)
            parts.Add($"{NamesOf(parents)} will include {target.Name}.");
        parts.Add($"{source.Name} is deleted.");
        return string.Join(' ', parts);
    }

    private async Task MergeAsync()
    {
        if (Original is not { } source || mergeTarget.Count != 1)
            return;

        busy = true;
        saveError = notice = null;
        try
        {
            var target = await Api.Merge(source.Id, mergeTarget[0]);
            await Directory.ReloadAsync();
            Edit(Directory.Find(target.Id) ?? target);
            notice = $"Merged \"{source.Name}\" into \"{target.Name}\".";
        }
        catch (Exception ex)
        {
            saveError = DescribeError(ex);
            confirmMerge = false;
            await Directory.ReloadAsync();
        }
        finally
        {
            busy = false;
        }
    }

    private string DeleteSummary(TagDto tag)
    {
        var parts = new List<string> { tag.ModCount == 0 ? "No mod has it." : $"{Mods(tag.ModCount)} lose it." };
        var children = Directory.Graph.ChildrenOf(tag.Id);
        if (children.Count > 0 && tag.ParentIds.Count > 0)
            parts.Add($"{NamesOf(children)} will no longer count as {NamesOf(tag.ParentIds)}; merge instead to keep that.");
        else if (children.Count > 0)
            parts.Add($"It includes {NamesOf(children)}; they stay.");
        return string.Join(' ', parts);
    }

    private async Task DeleteAsync()
    {
        if (Original is not { } tag)
            return;

        busy = true;
        saveError = notice = null;
        try
        {
            await Api.Delete(tag.Id);
            await Directory.ReloadAsync();
            Cancel();
            notice = $"Deleted \"{tag.Name}\".";
        }
        catch (Exception ex)
        {
            saveError = DescribeError(ex);
            confirmDelete = false;
        }
        finally
        {
            busy = false;
        }
    }

    private static string Mods(int count) => count == 1 ? "1 mod" : $"{count} mods";

    /// <summary>The server's reason (a JSON string in the body), or the exception's message.</summary>
    private static string DescribeError(Exception ex)
    {
        if (ex is not ApiException { ResponseBody: { Length: > 0 } body })
            return ex.Message;

        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
