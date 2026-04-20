using System.Collections;
using System.Reflection;

namespace F3M.Shared;

public static class Configuration
{
    public static string HeaderDisclaimer =>
#if DEBUG
        $"DEVELOPMENT {BuildTimeStamp}";
#else
        "Public Alpha {BuildTimeStamp}";
#endif
    public static readonly string BuildTimeStamp = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>() is { } aiva
        ? aiva.InformationalVersion
        : $"No Build info";

    //if (DateTime.TryParse(timeString, out var dt))
    //    return $"Build {dt:yyyy-MM-dd HH:mm:ss}";

    public static readonly string[] Categories = ["BepInEx-Plugin", "Custom-Missions 1", "Custom-Missions 2", "Cosplay-Loader", "Texture Edits", "Others"];
    public static readonly string[] AllowedFileFormat = [".zip", ".rar", ".7z", ".pak", ".mod"];
    public static readonly string[] AllowedThumbnailFormat = [".jpg", ".jpeg", ".png", ".webp"];
}
