namespace F3M.Server.Statistics;

/// <summary>Recognizes requests from the F3M desktop app, which names itself and its version in the User-Agent.</summary>
public static class DesktopClient
{
    private const string Product = "F3M-Desktop/";

    /// <summary>The desktop app's version when the request comes from it, else null.</summary>
    public static string? Version(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (!userAgent.StartsWith(Product, StringComparison.OrdinalIgnoreCase))
            return null;

        var version = userAgent[Product.Length..].Split(' ', 2)[0];
        return version.Length > 0 ? version : "unknown";
    }
}
