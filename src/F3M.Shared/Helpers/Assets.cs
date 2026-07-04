namespace F3M.Shared.Helpers;

/// <summary>
/// Assets and file paths used by both client and server.
/// </summary>
public static class Assets
{
    public static string ServerStorage => "Storage";
    public static string PublicDir => Path.Combine(ServerStorage, nameof(PublicDir));

    public static string FileDir => Path.Combine(PublicDir, nameof(FileDir));
    public static string ImageDir => Path.Combine(PublicDir, nameof(ImageDir));

    public static string FilePath(this string fileName) => Path.Combine(FileDir, fileName);
    public static string ImagePath(this string fileName) => Path.Combine(ImageDir, fileName);
}
