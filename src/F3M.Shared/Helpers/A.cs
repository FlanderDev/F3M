namespace F3M.Shared.Helpers;

public static class A
{
    public static string AssetDir { get; set; } = string.Empty;
    public static string FileDir { get; set; } = Path.Combine(A.AssetDir, nameof(FileDir));
    public static string ImageDir { get; set; } = Path.Combine(A.AssetDir, nameof(ImageDir));

    public static string FilePath(this string fileName) => $"{A.FileDir}/{fileName}";
    public static string ImagePath(this string fileName) => $"{A.ImageDir}/{fileName}";
}
