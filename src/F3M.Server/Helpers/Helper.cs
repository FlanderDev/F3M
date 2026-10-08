using System.Security.Claims;
using F3M.Shared.Helpers;

namespace F3M.Server.Helpers;

public static class Helper
{
    internal static int? GetUserId(ClaimsPrincipal User)
    {
        var result = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (int?)null;
        return result ?? null;
    }

    /// <summary>
    /// Install paths are relative to the game folder and end up on other people's machines. The rules live
    /// in <see cref="InstallPaths"/>, shared with the upload preview and server validation. Returns the
    /// path with '/' separators; empty input is valid.
    /// </summary>
    internal static bool TryNormalizeInstallPath(string? input, out string normalized) =>
        InstallPaths.TryNormalize(input, out normalized);

    /// <summary>
    /// The client chooses the "original name" of an uploaded file. Keep only a plain file name, and make
    /// sure its extension is the one of the file that was really uploaded (the extension is used for
    /// upload validation and install previews).
    /// </summary>
    internal static string SafeOriginalName(string? supplied, string uploadedFileName)
    {
        var actual = Path.GetFileName(uploadedFileName.Replace('\\', '/'));
        var leaf = Path.GetFileName((supplied ?? string.Empty).Replace('\\', '/')).Trim();

        if (leaf.Length == 0
            || leaf.Length > 200
            || !string.Equals(Path.GetExtension(leaf), Path.GetExtension(actual), StringComparison.OrdinalIgnoreCase))
        {
            return actual;
        }

        return leaf;
    }
}