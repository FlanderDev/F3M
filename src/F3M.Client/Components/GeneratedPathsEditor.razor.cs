using F3M.Client.Business;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components;

/// <summary>
/// Editor for the files a mod creates at runtime (plan 5.8): pattern rows with a kind, a check against the files
/// this upload installs itself, and a path tester. The list is edited in place.
/// </summary>
public partial class GeneratedPathsEditor
{
    /// <summary>The live list of patterns. Pass the same list instance on every render.</summary>
    [Parameter, EditorRequired]
    public List<GeneratedPathDto> Entries { get; set; } = [];

    /// <summary>Game-relative paths this upload installs itself. A pattern must not match any of them.</summary>
    [Parameter]
    public IReadOnlyList<string> ShippedPaths { get; set; } = [];

    /// <summary>Raised after any change so the parent can re-render.</summary>
    [Parameter] public EventCallback OnEntriesChanged { get; set; }

    private string checkPath = string.Empty;

    private bool CanAdd => Entries.Count < GeneratedPathRules.MaxPatternsPerVersion;

    private async Task Add()
    {
        if (!CanAdd)
            return;

        Entries.Add(new GeneratedPathDto());
        await OnEntriesChanged.InvokeAsync();
    }

    private async Task Remove(int index)
    {
        if (index >= 0 && index < Entries.Count)
        {
            Entries.RemoveAt(index);
            await OnEntriesChanged.InvokeAsync();
        }
    }

    private async Task PatternChanged(GeneratedPathDto entry, ChangeEventArgs e)
    {
        entry.Pattern = e.Value?.ToString() ?? string.Empty;
        await OnEntriesChanged.InvokeAsync();
    }

    private async Task KindChanged(GeneratedPathDto entry, ChangeEventArgs e)
    {
        if (Enum.TryParse<GeneratedPathKind>(e.Value?.ToString(), out var kind))
        {
            entry.Kind = kind;
            await OnEntriesChanged.InvokeAsync();
        }
    }

    private string TestResult
    {
        get
        {
            var path = checkPath.Trim();
            if (path.Length == 0)
                return string.Empty;

            var index = GeneratedPathChecks.FirstMatch(Entries, path);
            return index is { } i
                ? $"Matches pattern {i + 1}, kind: {KindLabel(Entries[i].Kind)}."
                : "No pattern matches this path.";
        }
    }

    private static string KindLabel(GeneratedPathKind kind) => kind switch
    {
        GeneratedPathKind.Generated => "generated",
        GeneratedPathKind.Cache => "cache",
        GeneratedPathKind.UserData => "user data",
        _ => kind.ToString(),
    };

    private static string KindHint(GeneratedPathKind kind) => kind switch
    {
        GeneratedPathKind.Generated => "Removed when the mod leaves a profile, after the user confirms.",
        GeneratedPathKind.Cache => "Disposable. Removed without asking.",
        GeneratedPathKind.UserData => "Holds the player's data, such as saves. Never removed.",
        _ => string.Empty,
    };
}
