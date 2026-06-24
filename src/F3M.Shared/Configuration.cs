using System.Reflection;

namespace F3M.Shared;

/// <remarks>
/// Provides configuration settings for the application.
/// DO NOT CHANGE FROM CLIENT OR SERVER SIDE, AS THEY ARE SEPERATE!
/// </remarks>
public static class Configuration
{
    public static readonly string BuildTimeStamp = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>() is { } aiva
        ? aiva.InformationalVersion.Split('+').Last()
        : $"No Build info";

    public static string HeaderDisclaimer =>
#if DEBUG
        $"DEVELOPMENT {BuildTimeStamp}";
#else
        $"Public Alpha {BuildTimeStamp}";
#endif

    public const string AppName = nameof(F3M);

    public static readonly string[] DefaultCategories = ["BepInEx-Plugin", "Custom-Missions 1", "Custom-Missions 2", "Cosplay-Loader", "Texture Edits", "Others"];
    public static readonly string[] AllowedFileExtension = [".dll", ".zip", ".rar", ".7z", ".pak", ".mod"];
    public static readonly string[] AllowedThumbnailExtension = [".jpg", ".jpeg", ".png", ".webp"];

    public const int ModDescriptionMaxSize = 10_000;

    public const long MaxModSize = 512 * 1024 * 1024;
    public const long MaxImageSize = 8 * 1024 * 1024;
    public const long MaxTotalSize = 1L * 1024 * 1024 * 1024; // 1 GB total per upload

    public enum SortBy
    {
        Newest,
        Oldest,
        DownloadsAsc,
        DownloadsDesc,
        NameAsc,
        NameDesc
    }
}
