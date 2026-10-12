using F3M.Client.Models;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Client.Business;

/// <summary>The upload form's optional groups, checked: what blocks the upload, notes, and the groups as the server will see them.</summary>
/// <param name="Error">Blocks the upload when set.</param>
/// <param name="Report">What the groups cover; null while they are not valid.</param>
/// <param name="Normalized">The valid groups, normalised. Used for the preview and the type checks even while one group is invalid.</param>
public sealed record OptionalGroupsCheck(string? Error, OptionalFileReport? Report, List<OptionalGroupDto> Normalized);

/// <summary>
/// Checks and edits the optional groups of the upload form. The rules are <see cref="OptionalFileRules"/>, the same the
/// server applies; this only gives immediate feedback and the "make optional" actions of the install preview.
/// </summary>
public static class OptionalGroupChecks
{
    public static OptionalGroupsCheck Evaluate(IReadOnlyList<OptionalGroupDraft> drafts, IReadOnlyCollection<string> shippedPaths)
    {
        var dtos = drafts.Select(d => d.ToDto()).ToList();
        if (!OptionalFileRules.TryNormalize(dtos, out var normalized, out var error))
            return new OptionalGroupsCheck(error, null, ValidGroups(dtos));

        var report = OptionalFileRules.Analyse(normalized, shippedPaths);
        return new OptionalGroupsCheck(report.Errors.FirstOrDefault(), report, normalized);
    }

    /// <summary>Every group that is valid on its own, so a half-typed group does not hide the others in the preview.</summary>
    private static List<OptionalGroupDto> ValidGroups(IEnumerable<OptionalGroupDto> groups)
    {
        var valid = new List<OptionalGroupDto>();
        foreach (var group in groups)
        {
            if (OptionalFileRules.TryNormalize([.. valid, group], out var normalized, out _))
                valid = normalized;
        }

        return valid;
    }

    /// <summary>
    /// Puts a preview entry (a file, or a folder with everything below it) into <paramref name="groupName"/>, creating
    /// the group when needed. The entry's exact pattern leaves any other group.
    /// </summary>
    public static void Assign(List<OptionalGroupDraft> drafts, string gamePath, bool isFolder, string groupName)
    {
        var pattern = OptionalFileRules.PatternFor(gamePath, isFolder);
        RemovePattern(drafts, pattern);

        var group = drafts.FirstOrDefault(d => string.Equals(d.Name.Trim(), groupName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new OptionalGroupDraft { Name = groupName };
            drafts.Add(group);
        }

        group.IsSuggestion = false;
        if (!group.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            group.Patterns.Add(pattern);
    }

    /// <summary>
    /// Takes a preview entry out of its group, when the group names it exactly. A group left without patterns is removed.
    /// False when only a wider pattern covers the entry; that pattern has to be edited in the group itself.
    /// </summary>
    public static bool Unassign(List<OptionalGroupDraft> drafts, string gamePath, bool isFolder) =>
        RemovePattern(drafts, OptionalFileRules.PatternFor(gamePath, isFolder));

    /// <summary>True when one of the groups names this entry exactly, so the preview can take it out again.</summary>
    public static bool IsNamedExactly(IReadOnlyList<OptionalGroupDraft> drafts, string gamePath, bool isFolder)
    {
        var pattern = OptionalFileRules.PatternFor(gamePath, isFolder);
        return drafts.Any(d => d.Patterns.Any(p => SamePattern(p, pattern)));
    }

    /// <summary>A name for a new group that no group has yet: "Optional files", "Optional files 2", ...</summary>
    public static string NewGroupName(IReadOnlyList<OptionalGroupDraft> drafts)
    {
        const string name = "Optional files";
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? name : $"{name} {i}";
            if (!drafts.Any(d => string.Equals(d.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private static bool RemovePattern(List<OptionalGroupDraft> drafts, string pattern)
    {
        var removed = false;
        foreach (var draft in drafts.ToList())
        {
            if (draft.Patterns.RemoveAll(p => SamePattern(p, pattern)) == 0)
                continue;

            removed = true;
            draft.IsSuggestion = false;
            if (draft.Patterns.Count == 0)
                drafts.Remove(draft);
        }

        return removed;
    }

    private static bool SamePattern(string raw, string pattern) =>
        GeneratedPathRules.TryNormalize(raw, out var normalized, out _) && string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase);
}
