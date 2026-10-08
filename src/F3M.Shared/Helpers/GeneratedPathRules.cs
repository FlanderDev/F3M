namespace F3M.Shared.Helpers;

/// <summary>
/// Rules for generated-file patterns (plan 5.8). Used by the upload form, the server, and the desktop app,
/// so every side accepts and matches exactly the same patterns.
///
/// Grammar: '*' matches within one path segment, '?' matches one character, and '**' matches any number of
/// whole segments. Paths use '/' separators and are compared case-insensitively on every platform.
/// </summary>
public static class GeneratedPathRules
{
    public const int MaxLength = 260;
    public const int MaxPatternsPerVersion = 20;

    /// <summary>A pattern must name at least this many folders before its first wildcard, so it cannot cover a whole root.</summary>
    public const int MinLiteralSegments = 2;

    /// <summary>Top-level folders a pattern may start with. Add a game's save folder here once it has been confirmed.</summary>
    public static readonly string[] AllowedRoots = ["BepInEx"];

    /// <summary>Validates a pattern and returns it normalised: '/' separators, trimmed, no '.' segments.</summary>
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "A generated-file pattern cannot be empty.";
            return false;
        }

        var raw = input.Trim().Replace('\\', '/');
        if (raw.Length > MaxLength)
        {
            error = $"A generated-file pattern can be at most {MaxLength} characters long.";
            return false;
        }

        if (raw.StartsWith('/') || (raw.Length >= 2 && raw[1] == ':'))
        {
            error = $"The pattern '{raw}' must be relative to the game folder.";
            return false;
        }

        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToArray();

        if (segments.Length == 0)
        {
            error = "A generated-file pattern cannot be empty.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                error = $"The pattern '{raw}' must not contain '..'.";
                return false;
            }

            if (segment.Contains("**") && segment != "**")
            {
                error = $"In '{raw}', '**' must be a whole path segment.";
                return false;
            }

            // Wildcards are not valid in file names, so check the parts around them with the install-path rules.
            var literal = segment.Replace("*", string.Empty).Replace("?", string.Empty);
            if (!InstallPaths.TrySplit(literal, out _, out _))
            {
                error = $"The pattern '{raw}' contains a name that is not allowed in a file path.";
                return false;
            }
        }

        if (!AllowedRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            error = $"The pattern '{raw}' must start with one of: {string.Join(", ", AllowedRoots)}.";
            return false;
        }

        var literalPrefix = segments.TakeWhile(s => !HasWildcard(s)).Count();
        if (literalPrefix < MinLiteralSegments)
        {
            error = $"The pattern '{raw}' is too broad. Name at least {MinLiteralSegments} folders before the first wildcard.";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>True when a game-relative path matches the pattern.</summary>
    public static bool IsMatch(string pattern, string gamePath) =>
        MatchSegments(Split(pattern), 0, Split(gamePath), 0);

    /// <summary>
    /// Conservative overlap test between two patterns: returns true whenever some path could match both.
    /// It may return true for patterns that never really overlap, which is acceptable for a warning.
    /// </summary>
    public static bool MayOverlap(string a, string b) =>
        MayOverlapSegments(Split(a), 0, Split(b), 0);

    private static bool HasWildcard(string segment) => segment.Contains('*') || segment.Contains('?');

    private static string[] Split(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(string[] pattern, int p, string[] path, int t)
    {
        while (true)
        {
            if (p == pattern.Length)
                return t == path.Length;

            if (pattern[p] == "**")
            {
                // '**' may consume zero or more whole segments.
                for (var skip = t; skip <= path.Length; skip++)
                {
                    if (MatchSegments(pattern, p + 1, path, skip))
                        return true;
                }

                return false;
            }

            if (t == path.Length || !SegmentMatches(pattern[p], path[t]))
                return false;

            p++;
            t++;
        }
    }

    private static bool MayOverlapSegments(string[] a, int i, string[] b, int j)
    {
        if (i == a.Length && j == b.Length)
            return true;

        if (i < a.Length && a[i] == "**")
            return MayOverlapSegments(a, i + 1, b, j) || (j < b.Length && MayOverlapSegments(a, i, b, j + 1));

        if (j < b.Length && b[j] == "**")
            return MayOverlapSegments(a, i, b, j + 1) || (i < a.Length && MayOverlapSegments(a, i + 1, b, j));

        if (i == a.Length || j == b.Length)
            return false;

        return SegmentsMayMatch(a[i], b[j]) && MayOverlapSegments(a, i + 1, b, j + 1);
    }

    private static bool SegmentsMayMatch(string x, string y)
    {
        var xWild = HasWildcard(x);
        var yWild = HasWildcard(y);

        if (!xWild && !yWild)
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        if (!xWild)
            return SegmentMatches(y, x);
        if (!yWild)
            return SegmentMatches(x, y);

        // Two wildcard segments: assume they can meet.
        return true;
    }

    /// <summary>Matches one path segment against a pattern segment containing '*' and '?'. Case-insensitive.</summary>
    private static bool SegmentMatches(string pattern, string text)
    {
        var p = 0;
        var t = 0;
        var starP = -1;
        var starT = -1;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }
}
