namespace F3M.Shared.Helpers;

/// <summary>
/// Links the website hands to the F3M desktop app (f3m://, plan 9.2). Each names the server it is about
/// (<c>server=</c>), so the app looks the mod up on the right site; the app asks before it trusts a site for the
/// first time. Collections have their own link, <see cref="CollectionLink.ToProtocolUri"/>.
/// </summary>
public static class DesktopLink
{
    public const string ServerParameter = "server";

    /// <summary>
    /// Downloads a mod in the desktop app: the version <paramref name="versionId"/>, or the latest when it is null.
    /// E.g. <c>f3m://v1/download?mod=45&amp;server=https%3A%2F%2Ff3m.example</c>.
    /// </summary>
    public static string Download(int groupId, int? versionId, string server)
    {
        var link = versionId is { } version ? $"f3m://v1/download?mod={groupId}&version={version}" : $"f3m://v1/download?mod={groupId}";
        return WithServer(link, server);
    }

    /// <summary>Adds the <c>server</c> parameter: the site's origin, e.g. https://f3m.example (no path).</summary>
    public static string WithServer(string link, string server)
    {
        if (!TryNormalizeServer(server, out var origin))
            throw new ArgumentException($"'{server}' is not a server address.", nameof(server));

        var separator = link.Contains('?') ? '&' : '?';
        return $"{link}{separator}{ServerParameter}={Uri.EscapeDataString(origin)}";
    }

    /// <summary>
    /// A server address as its origin: http(s), lower-case host, the port only when it is not the default, no path,
    /// query or user info. Used to compare the server in a link with the one the app uses.
    /// </summary>
    public static bool TryNormalizeServer(string? address, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath.Trim('/').Length > 0
            || uri.Host.Length == 0)
            return false;

        origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }
}
