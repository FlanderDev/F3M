using System.Text.RegularExpressions;
using F3M.Shared.Models;

namespace F3M.Shared.Helpers;

/// <summary>What an upload's optional groups cover: problems that block it, notes, and how many files each group has.</summary>
/// <param name="Errors">Block the upload.</param>
/// <param name="Warnings">Shown, but the upload goes ahead.</param>
/// <param name="FilesPerGroup">Matched files per group name.</param>
/// <param name="Installed">Files that are always installed.</param>
public sealed record OptionalFileReport(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int> FilesPerGroup,
    int Installed);

/// <summary>
/// Rules for optional groups: named parts of a version (examples, tutorials, documentation) that the mod does not need.
/// Used by the upload form, the server and the desktop app, so every side accepts and matches the same groups.
///
/// A group's patterns use the generated-file grammar (<see cref="GeneratedPathRules"/>) and are matched against the
/// game-relative paths the version installs. A path belongs to the first group, in order, with a matching pattern.
/// </summary>
public static partial class OptionalFileRules
{
    public const int MaxGroupsPerVersion = 10;
    public const int MaxPatternsPerGroup = 30;
    public const int MaxNameLength = 40;

    /// <summary>How patterns are called in error messages, which already name the group.</summary>
    public const string PatternNoun = "pattern";

    /// <summary>A group name: trimmed, inner whitespace collapsed, 1 to <see cref="MaxNameLength"/> characters.</summary>
    public static bool TryNormalizeName(string? input, out string normalized, out string? error)
    {
        normalized = Whitespace().Replace(input ?? string.Empty, " ").Trim();
        error = null;

        if (normalized.Length == 0)
            error = "An optional group needs a name, such as Examples.";
        else if (normalized.Length > MaxNameLength)
            error = $"The group name '{normalized}' is longer than {MaxNameLength} characters.";
        else if (normalized.Any(char.IsControl))
            error = $"The group name '{normalized}' contains characters that are not allowed.";

        return error is null;
    }

    /// <summary>
    /// Checks the groups of one version and returns them normalised: valid unique names, at least one valid pattern
    /// each, no pattern twice. The first problem is returned as <paramref name="error"/>.
    /// </summary>
    public static bool TryNormalize(IReadOnlyList<OptionalGroupDto> groups, out List<OptionalGroupDto> normalized, out string? error)
    {
        normalized = [];
        error = null;

        if (groups.Count > MaxGroupsPerVersion)
        {
            error = $"A version can have at most {MaxGroupsPerVersion} optional groups.";
            return false;
        }

        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (!TryNormalizeName(group.Name, out var name, out error))
                return false;

            if (normalized.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"There are two optional groups called '{name}'.";
                return false;
            }

            if (group.Patterns.Count == 0)
            {
                error = $"The optional group '{name}' has no files. Add a pattern or remove the group.";
                return false;
            }

            if (group.Patterns.Count > MaxPatternsPerGroup)
            {
                error = $"The optional group '{name}' has more than {MaxPatternsPerGroup} patterns. Use a folder pattern such as Examples/**.";
                return false;
            }

            var clean = new List<string>();
            foreach (var raw in group.Patterns)
            {
                if (!GeneratedPathRules.TryNormalize(raw, out var pattern, out var patternError, PatternNoun))
                {
                    error = $"Optional group '{name}': {patternError}";
                    return false;
                }

                if (!patterns.Add(pattern))
                {
                    error = $"The pattern '{pattern}' is listed more than once in the optional groups.";
                    return false;
                }

                clean.Add(pattern);
            }

            normalized.Add(new OptionalGroupDto { Name = name, Patterns = clean });
        }

        return true;
    }

    /// <summary>The group a game path belongs to, or null when it is always installed.</summary>
    public static string? GroupOf(IEnumerable<OptionalGroupDto> groups, string gamePath) =>
        groups.FirstOrDefault(g => g.Patterns.Any(p => GeneratedPathRules.IsMatch(p, gamePath)))?.Name;

    /// <summary>The group a game path belongs to, or null when it is always installed.</summary>
    public static string? GroupOf(IEnumerable<OptionalGroupRef> groups, string gamePath) =>
        groups.FirstOrDefault(g => g.Patterns.Any(p => GeneratedPathRules.IsMatch(p, gamePath)))?.Name;

    /// <summary>
    /// True when a placement goes into the game folder: it is in no group, or in one of <paramref name="enabled"/>
    /// (the groups the player turned on).
    /// </summary>
    public static bool IsInstalled(IEnumerable<OptionalGroupRef>? groups, string gamePath, IEnumerable<string>? enabled)
    {
        var group = groups is null ? null : GroupOf(groups, gamePath);
        return group is null || (enabled?.Contains(group, StringComparer.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The game paths that are always installed: <paramref name="targets"/> without the optional ones.</summary>
    public static List<string> Installed(IEnumerable<OptionalGroupDto> groups, IEnumerable<string> targets)
    {
        var list = groups.ToList();
        return [.. targets.Where(t => GroupOf(list, t) is null)];
    }

    /// <summary>
    /// What normalised <paramref name="groups"/> cover of the paths a version installs. Every file being optional is
    /// an error (nothing would be installed); a group or pattern that matches no file is a warning.
    /// </summary>
    public static OptionalFileReport Analyse(IReadOnlyList<OptionalGroupDto> groups, IReadOnlyCollection<string> targets)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var perGroup = groups.ToDictionary(g => g.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var installed = 0;

        foreach (var target in targets)
        {
            if (GroupOf(groups, target) is { } group)
                perGroup[group]++;
            else
                installed++;
        }

        if (groups.Count > 0 && targets.Count > 0 && installed == 0)
            errors.Add("Every file is in an optional group, so nothing would be installed. Keep the files the mod needs out of the groups.");

        foreach (var group in groups)
        {
            if (perGroup[group.Name] == 0)
            {
                warnings.Add($"The optional group '{group.Name}' matches none of the files.");
                continue;
            }

            foreach (var pattern in group.Patterns.Where(p => !targets.Any(t => GeneratedPathRules.IsMatch(p, t))))
                warnings.Add($"'{pattern}' in the optional group '{group.Name}' matches none of the files.");
        }

        return new OptionalFileReport(errors, warnings, perGroup, installed);
    }

    /// <summary>A pattern for one preview entry: the file itself, or everything below a folder.</summary>
    public static string PatternFor(string gamePath, bool isFolder) =>
        isFolder ? $"{gamePath.TrimEnd('/')}/**" : gamePath;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
