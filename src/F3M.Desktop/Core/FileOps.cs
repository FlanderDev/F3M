using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F3M.Shared.Helpers;

namespace F3M.Desktop.Core;

/// <summary>Message shown to the user as is. Anything else is logged and shown as a generic failure.</summary>
public sealed class UserException(string message) : Exception(message);

/// <summary>File helpers shared by the services: atomic writes, hashing, and paths that must stay inside a root.</summary>
public static class FileOps
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Compact form, used for journal lines and documents that are hashed.</summary>
    public static readonly JsonSerializerOptions JsonCompact = new(JsonSerializerDefaults.Web);

    /// <summary>Returns the default when the file does not exist. A corrupt file throws, so the caller can say so.</summary>
    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
    }

    public static void WriteJsonAtomic<T>(string path, T value)
    {
        WriteTextAtomic(path, JsonSerializer.Serialize(value, Json));
    }

    public static void WriteTextAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Resolves a game-relative or cache-relative path under <paramref name="root"/>. Rejects "..", absolute paths and
    /// drive letters (through <see cref="InstallPaths"/>), and any result that is not inside the root.
    /// </summary>
    public static string ResolveUnder(string root, string relative)
    {
        if (relative.StartsWith('/') || relative.StartsWith('\\') || (relative.Length >= 2 && relative[1] == ':'))
            throw new UserException($"'{relative}' must be a relative path.");
        if (!InstallPaths.TrySplit(relative, out var parts, out var bad) || parts.Count == 0)
            throw new UserException($"'{relative}' is not a valid path ({bad ?? "empty"}).");

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar, parts)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot, comparison))
            throw new UserException($"'{relative}' would be written outside the game folder.");
        return full;
    }

    /// <summary>True when any existing directory or file between the root and the path is a symlink or junction.</summary>
    public static bool HasReparsePointBelow(string root, string fullPath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current) && current.Length > fullRoot.Length)
        {
            try
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (IOException)
            {
                // Unreadable folders are treated as not reparse points; the write itself will fail if it matters.
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    public static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
