namespace F3M.Shared.Helpers;

/// <summary>
/// Links the website hands to the F3M desktop app (f3m://, plan 9.2). The desktop app asks the user to confirm every
/// one before it acts. Collections have their own link, <see cref="CollectionLink.ToProtocolUri"/>.
/// </summary>
public static class DesktopLink
{
    /// <summary>
    /// Downloads a mod in the desktop app: the version <paramref name="versionId"/>, or the latest when it is null.
    /// E.g. <c>f3m://v1/download?mod=45</c>.
    /// </summary>
    public static string Download(int groupId, int? versionId = null) =>
        versionId is { } version ? $"f3m://v1/download?mod={groupId}&version={version}" : $"f3m://v1/download?mod={groupId}";
}
