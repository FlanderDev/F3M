namespace F3M.Shared;

public static class Configuration
{
    public const string HeaderDisclaimer =
#if DEBUG
        "DEVELOPMENT";
#else
        "Public Alpha";
#endif
    public static readonly string[] Categories = ["BepInEx-Plugin", "Custom-Missions 1", "Custom-Missions 2", "Cosplay-Loader", "Texture Edits", "Others"];
    public static readonly string[] AllowedFileFormat = [".zip", ".rar", ".7z", ".pak", ".mod"];
    public static readonly string[] AllowedThumbnailFormat = [".jpg", ".jpeg", ".png", ".webp"];
}
