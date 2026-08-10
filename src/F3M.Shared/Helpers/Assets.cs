namespace F3M.Shared.Helpers;

/// <summary>
/// Assets and file paths used by both client and server.
/// </summary>
public static class Assets
{
    #region Server-side filesystem paths
    public static string StorageRoot { get; set; } = "Storage";
    public static string PublicContent { get; set; } = Path.Combine(StorageRoot, nameof(PublicContent));
    public static string Files { get; set; } = Path.Combine(PublicContent, nameof(Files));
    public static string Images { get; set; } = Path.Combine(PublicContent, nameof(Images));

    public static string FilePath(this string fileName) => Path.Combine(Files, fileName);
    public static string ImagePath(this string fileName) => Path.Combine(Images, fileName);
    #endregion

    #region Browser-facing URLs
    public const string ServedPath = $"/{nameof(Assets)}";
    public static string FileUrl(this string fileName) => $"{ServedPath}/{nameof(Files)}/{fileName}";
    public static string ImageUrl(this string fileName) => $"{ServedPath}/{nameof(Images)}/{fileName}";
    #endregion
}
