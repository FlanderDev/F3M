using F3M.Shared.Models;

namespace F3M.Server.Statistics;

/// <summary>
/// Tells what sent a request: the website in a browser, the desktop app, or anything else (scripts, bots, crawlers).
/// The statistics count them apart, so a script fetching mods doesn't pass for visitors.
/// </summary>
public static class RequestClients
{
    // The desktop app names itself and its version in the User-Agent.
    private const string DesktopProduct = "F3M-Desktop/";

    // What bots and automated browsers put in their User-Agent.
    private static readonly string[] BotWords = ["bot", "crawl", "spider", "slurp", "headless"];
    private static readonly string[] Browsers = ["Safari", "Chrome", "Firefox", "Edg", "OPR"];

    public static RequestClient Of(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (userAgent.StartsWith(DesktopProduct, StringComparison.OrdinalIgnoreCase))
            return RequestClient.Desktop;
        return IsWebsite(request, userAgent) ? RequestClient.Website : RequestClient.Other;
    }

    /// <summary>The desktop app's version when the request comes from it, else null.</summary>
    public static string? DesktopVersion(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (!userAgent.StartsWith(DesktopProduct, StringComparison.OrdinalIgnoreCase))
            return null;

        var version = userAgent[DesktopProduct.Length..].Split(' ', 2)[0];
        return version.Length > 0 ? version : "unknown";
    }

    /// <summary>
    /// A short name for a script or bot: the program it names in its User-Agent (curl, python-requests, Googlebot …).
    /// </summary>
    public static string ScriptName(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString().Trim();
        if (userAgent.Length == 0)
            return "(no user agent)";

        var products = userAgent.Split([' ', ';', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Contains("://"))
            .Select(t => t.TrimStart('+').Split('/')[0])
            .Where(t => t.Length > 0)
            .ToList();
        if (products.Count == 0)
            return "(no user agent)";

        // A bot names itself inside a browser-like agent: "Mozilla/5.0 (compatible; Googlebot/2.1; …)".
        if (products.FirstOrDefault(IsBotWord) is { } bot)
            return bot;
        if (products[0] != "Mozilla")
            return products[0];

        // Browser-like, without the headers a browser sends from the site: a script dressed up as a browser, or a tool
        // that names itself last ("… PowerShell/7.4").
        return Browsers.Contains(products[^1]) ? "Browser-like" : products[^1];
    }

    /// <summary>
    /// The website's requests come from a browser on this site. The browser marks them same-origin (Sec-Fetch-Site,
    /// which no page can set), and the website adds X-Requested-With; browsers too old for Sec-Fetch-Site (Safari
    /// before 16.4) are recognized by that alone.
    /// </summary>
    private static bool IsWebsite(HttpRequest request, string userAgent)
    {
        if (!userAgent.StartsWith("Mozilla/", StringComparison.Ordinal) || BotWords.Any(w => userAgent.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return false;

        var site = request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length > 0
            ? site is "same-origin" or "same-site"
            : request.Headers.XRequestedWith == "XMLHttpRequest";
    }

    private static bool IsBotWord(string product) => BotWords.Any(w => product.Contains(w, StringComparison.OrdinalIgnoreCase));
}
