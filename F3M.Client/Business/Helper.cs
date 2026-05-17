using Microsoft.AspNetCore.Components.Authorization;

namespace F3M.Client.Business;

public static class Helper
{
    internal static async Task<bool> IsAuthenticatedAsync(this Task<AuthenticationState>? authenticationState)
    {
        if (authenticationState is null)
            return false;
        
        return (await authenticationState)?.User.Identity?.IsAuthenticated ?? false;
    }

    public static string FileSize(this long bytes) =>
        bytes >= 1_048_576
            ? $"{bytes / 1_048_576.0:F1} MB"
            : $"{bytes / 1024.0:F1} KB";
}