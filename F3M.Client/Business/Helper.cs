using Microsoft.AspNetCore.Components.Authorization;

namespace F3M.Client.Business;

public class Helper
{
    internal static async Task<bool> IsAuthenticatedAsync(Task<AuthenticationState>? authenticationState)
    {
        if (authenticationState is null) return false;
        var state = await authenticationState;
        return state?.User.Identity?.IsAuthenticated ?? false;
    }
}