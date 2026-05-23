namespace F3M.Shared.Helpers;

public static class A
{
    public const string Thumbnails = "previews";
    public const string Uploads = "uploads";

    public static string AssetPreview(this string fileName) => $"{Thumbnails}/{fileName}";
}
