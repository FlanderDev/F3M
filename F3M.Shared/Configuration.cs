using System.Reflection;

namespace F3M.Shared;

public static class Configuration
{
    public static readonly string BuildTimeStamp = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>() is { } aiva
        ? aiva.InformationalVersion.Split('+').Last()
        : $"No Build info";

    public static string HeaderDisclaimer =>
#if DEBUG
        $"DEVELOPMENT {BuildTimeStamp}";
#else
        "Public Alpha {BuildTimeStamp}";
#endif

    public const string AppName = nameof(F3M);
    public static readonly string[] Categories = ["BepInEx-Plugin", "Custom-Missions 1", "Custom-Missions 2", "Cosplay-Loader", "Texture Edits", "Others"];
    public static readonly string[] AllowedFileExtension = [".dll", ".zip", ".rar", ".7z", ".pak", ".mod"];
    public static readonly string[] AllowedThumbnailExtension = [".jpg", ".jpeg", ".png", ".webp"];
}
