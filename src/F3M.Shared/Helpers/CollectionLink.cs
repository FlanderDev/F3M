namespace F3M.Shared.Helpers;

/// <summary>
/// A shared collection (plan 10): a list of group IDs and an optional name. The same content is used for the
/// web link (/collection) and the protocol link (f3m://v1/import-profile). Nothing else is ever stored or sent.
/// </summary>
public sealed record CollectionLink(IReadOnlyList<int> GroupIds, string? Name)
{
    public const int MaxMods = 100;
    public const int MaxNameLength = 64;
    public const int MaxUriLength = 2048;

    private const string ProtocolPrefix = "f3m://v1/import-profile?";

    /// <summary>
    /// Parses the <c>mods</c> and <c>name</c> parameters under the rules of plan 9.2. Returns false with a
    /// readable <paramref name="error"/> when the collection is not valid.
    /// </summary>
    public static bool TryParse(string? mods, string? name, out CollectionLink? link, out string? error)
    {
        link = null;
        error = null;

        if (string.IsNullOrWhiteSpace(mods))
        {
            error = "No mods are listed.";
            return false;
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in mods.Split(','))
        {
            // Digits only: no signs, spaces, or leading garbage.
            if (part.Length is 0 or > 10 || !part.All(char.IsAsciiDigit) || !int.TryParse(part, out var id) || id <= 0)
            {
                error = $"'{part}' is not a valid mod ID.";
                return false;
            }
            if (!seen.Add(id))
            {
                error = $"Mod {id} is listed twice.";
                return false;
            }
            ids.Add(id);
            if (ids.Count > MaxMods)
            {
                error = $"A collection holds at most {MaxMods} mods.";
                return false;
            }
        }

        string? trimmed = null;
        if (name is not null)
        {
            trimmed = name.Trim();
            if (trimmed.Length > MaxNameLength)
            {
                error = $"The name is longer than {MaxNameLength} characters.";
                return false;
            }
            if (trimmed.Any(char.IsControl))
            {
                error = "The name contains control characters.";
                return false;
            }
            if (trimmed.Length == 0) trimmed = null;
        }

        var candidate = new CollectionLink(ids, trimmed);
        if (ProtocolPrefix.Length + candidate.ToQuery().Length > MaxUriLength)
        {
            error = "The collection is too long to share as a link.";
            return false;
        }

        link = candidate;
        return true;
    }

    /// <summary>The query string shared by both link kinds, e.g. <c>mods=45,67&amp;name=Hardcore</c>.</summary>
    public string ToQuery()
    {
        var query = "mods=" + string.Join(',', GroupIds);
        if (!string.IsNullOrEmpty(Name)) query += "&name=" + Uri.EscapeDataString(Name);
        return query;
    }

    /// <summary>Relative path of the web page, e.g. <c>/collection?mods=45,67</c>.</summary>
    public string ToWebPath() => "/collection?" + ToQuery();

    /// <summary>Protocol link opened by the desktop app, e.g. <c>f3m://v1/import-profile?mods=45,67</c>.</summary>
    public string ToProtocolUri() => ProtocolPrefix + ToQuery();
}
