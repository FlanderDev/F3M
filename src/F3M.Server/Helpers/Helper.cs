using System.Security.Claims;

namespace F3M.Server.Helpers;

public static class Helper
{
    internal static int? GetUserId(ClaimsPrincipal User)
    {
        var result = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (int?)null;
        return result ?? null;
    }
}