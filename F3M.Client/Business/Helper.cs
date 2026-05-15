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
}