using System.Text.RegularExpressions;

namespace F3M.Shared.Helpers;

/// <summary>
/// Parses F95zone profile URLs. Shared so the sign-up page can preview the username with exactly the server's rule.
/// </summary>
public static partial class F95Profile
{
    /// <summary>Shown wherever the user has to enter their profile URL.</summary>
    public const string ExampleUrl = "https://f95zone.to/members/YourName.12345/";

    // Matches: https://f95zone.to/members/username.12345/
    [GeneratedRegex(@"^https?://f95zone\.to/members/([a-zA-Z0-9_.\-]+?)\.(\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrlRegex();

    /// <summary>
    /// Reads the name and numeric ID from a profile URL. The name is the URL's spelling, which is also the F3M
    /// username a new account gets.
    /// </summary>
    public static bool TryParse(string? url, out string username, out string userId)
    {
        var match = ProfileUrlRegex().Match(url?.Trim() ?? string.Empty);
        username = match.Success ? match.Groups[1].Value : string.Empty;
        userId = match.Success ? match.Groups[2].Value : string.Empty;
        return match.Success;
    }

    public static string Url(string username, string userId) => $"https://f95zone.to/members/{username}.{userId}/";
}
