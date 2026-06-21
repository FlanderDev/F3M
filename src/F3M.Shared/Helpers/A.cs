namespace F3M.Shared.Helpers;

public static class A
{
    public static string AssetDir { get; set; } = "/assets";
    public static string FileDir => Path.Combine(AssetDir, nameof(FileDir));
    public static string ImageDir => Path.Combine(AssetDir, nameof(ImageDir));
    public static string FilePath(this string fileName) => Path.Combine(FileDir, fileName);
    public static string ImagePath(this string fileName) => Path.Combine(ImageDir, fileName);
}
