using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Client.Business;

/// <summary>The result of checking one generated-file pattern in the upload form.</summary>
/// <param name="Error">Blocks the upload when set.</param>
/// <param name="Warning">Shown but does not block the upload.</param>
public sealed record PatternCheck(string? Error, string? Warning);

/// <summary>
/// Checks the upload form's generated-file patterns before submit. The server runs the same rules and has the
/// final say; these checks only give immediate feedback.
/// </summary>
public static class GeneratedPathChecks
{
    public static IReadOnlyList<PatternCheck> Evaluate(IReadOnlyList<GeneratedPathDto> entries, IReadOnlyList<string> shippedPaths)
    {
        var results = new PatternCheck[entries.Count];
        var seen = new List<string>();

        if (entries.Count > GeneratedPathRules.MaxPatternsPerVersion)
        {
            var tooMany = $"A version can declare at most {GeneratedPathRules.MaxPatternsPerVersion} patterns.";
            for (var i = 0; i < results.Length; i++)
                results[i] = new PatternCheck(tooMany, null);
            return results;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (!GeneratedPathRules.TryNormalize(entries[i].Pattern, out var pattern, out var error))
            {
                results[i] = new PatternCheck(error, null);
                continue;
            }

            if (seen.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                results[i] = new PatternCheck($"The pattern '{pattern}' is listed more than once.", null);
                continue;
            }
            seen.Add(pattern);

            var clash = shippedPaths.FirstOrDefault(path => GeneratedPathRules.IsMatch(pattern, path));
            if (clash is not null)
            {
                results[i] = new PatternCheck($"This pattern would match '{clash}', which the mod installs itself. Narrow the pattern.", null);
                continue;
            }

            var warning = GeneratedPathRules.HasDeepWildcard(pattern)
                ? "Matches at any depth. Check that it cannot reach files other mods create."
                : null;
            results[i] = new PatternCheck(null, warning);
        }

        return results;
    }

    public static bool HasErrors(IReadOnlyList<PatternCheck> checks) => checks.Any(c => c.Error is not null);

    /// <summary>Index of the first pattern that matches a game path, or null when none does.</summary>
    public static int? FirstMatch(IReadOnlyList<GeneratedPathDto> entries, string gamePath)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (GeneratedPathRules.TryNormalize(entries[i].Pattern, out var pattern, out _)
                && GeneratedPathRules.IsMatch(pattern, gamePath))
            {
                return i;
            }
        }

        return null;
    }
}
