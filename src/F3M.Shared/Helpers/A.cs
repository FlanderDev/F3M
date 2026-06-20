namespace F3M.Shared.Helpers;

public static class A
{
    public static string AssetDir { get; set; } = string.Empty;
    public static string FileDir { get; set; } = Path.Combine(AssetDir, nameof(FileDir));
    public static string ImageDir { get; set; } = Path.Combine(AssetDir, nameof(ImageDir));

    public static string FilePath(this string fileName) => $"{FileDir}/{fileName}";
    public static string ImagePath(this string fileName) => $"{ImageDir}/{fileName}";
}
