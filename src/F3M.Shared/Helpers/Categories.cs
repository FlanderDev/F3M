namespace F3M.Shared.Helpers;

/// <summary>Mod categories as lists show them.</summary>
public static class Categories
{
    /// <summary>The catch-all category; lists show it last.</summary>
    public const string Others = "Others";

    /// <summary>Each category once (ignoring case), in the order given, with <see cref="Others"/> moved to the end.</summary>
    public static List<string> OthersLast(IEnumerable<string> categories) =>
    [
        .. categories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => string.Equals(c, Others, StringComparison.OrdinalIgnoreCase)),
    ];
}
