using F3M.Client.Business;
using F3M.Client.Models;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components;

/// <summary>
/// Editor for the optional groups of an upload: a name and patterns per group, how many files each covers, and what
/// blocks the upload. The list is edited in place; the install preview edits the same list.
/// </summary>
public partial class OptionalGroupsEditor
{
    /// <summary>The live list of groups. Pass the same list instance on every render.</summary>
    [Parameter, EditorRequired]
    public List<OptionalGroupDraft> Groups { get; set; } = [];

    /// <summary>Game-relative paths this upload installs.</summary>
    [Parameter]
    public IReadOnlyList<string> ShippedPaths { get; set; } = [];

    /// <summary>The result of <see cref="OptionalGroupChecks.Evaluate"/> for <see cref="Groups"/>, from the parent.</summary>
    [Parameter, EditorRequired]
    public OptionalGroupsCheck Check { get; set; } = default!;

    /// <summary>Names of suggested groups the author removed; they are not suggested again.</summary>
    [Parameter, EditorRequired]
    public HashSet<string> Dismissed { get; set; } = [];

    /// <summary>Raised after any change so the parent can re-render.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool CanAdd => Groups.Count < OptionalFileRules.MaxGroupsPerVersion;

    private int FileCount(OptionalGroupDraft group)
    {
        if (!OptionalFileRules.TryNormalizeName(group.Name, out var name, out _))
            return 0;
        if (Check.Report is { } report)
            return report.FilesPerGroup.GetValueOrDefault(name);
        return ShippedPaths.Count(path => string.Equals(OptionalFileRules.GroupOf(Check.Normalized, path), name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task Add()
    {
        if (!CanAdd)
            return;

        Groups.Add(new OptionalGroupDraft { Name = OptionalGroupChecks.NewGroupName(Groups), Patterns = [string.Empty] });
        await OnChanged.InvokeAsync();
    }

    private async Task RemoveGroup(int index)
    {
        if (index < 0 || index >= Groups.Count)
            return;

        if (Groups[index].IsSuggestion)
            Dismissed.Add(Groups[index].Name);
        Groups.RemoveAt(index);
        await OnChanged.InvokeAsync();
    }

    private async Task NameChanged(OptionalGroupDraft group, ChangeEventArgs e)
    {
        group.Name = e.Value?.ToString() ?? string.Empty;
        group.IsSuggestion = false;
        await OnChanged.InvokeAsync();
    }

    private async Task PatternChanged(OptionalGroupDraft group, int index, ChangeEventArgs e)
    {
        if (index >= group.Patterns.Count)
            return;

        group.Patterns[index] = e.Value?.ToString() ?? string.Empty;
        group.IsSuggestion = false;
        await OnChanged.InvokeAsync();
    }

    private async Task AddPattern(OptionalGroupDraft group)
    {
        group.Patterns.Add(string.Empty);
        group.IsSuggestion = false;
        await OnChanged.InvokeAsync();
    }

    private async Task RemovePattern(OptionalGroupDraft group, int groupIndex, int index)
    {
        if (index >= group.Patterns.Count)
            return;

        group.Patterns.RemoveAt(index);
        if (group.IsSuggestion)
            Dismissed.Add(group.Name);
        group.IsSuggestion = false;
        if (group.Patterns.Count == 0)
            Groups.RemoveAt(groupIndex);
        await OnChanged.InvokeAsync();
    }
}
