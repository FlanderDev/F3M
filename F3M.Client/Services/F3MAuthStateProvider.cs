using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace F3M.Client.Services;

public class F3MAuthStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    private const string StorageKey = "f3m_token";
    public string? Token { get; private set; }

    /// <summary>
    /// True when the cached token carries the Admin role claim.
    /// Cheap synchronous check — no await needed in components.
    /// </summary>
    public bool IsAdmin
    {
        get
        {
            if (string.IsNullOrEmpty(Token)) return false;
            var claims = ParseClaims(Token);
            // Match both the short JWT name ("role") and the long ClaimTypes.Role URI
            return claims?.Any(c =>
                (c.Type == ClaimTypes.Role || c.Type == "role") &&
                c.Value == "Admin") ?? false;
        }
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (Token is null)
        {
            try { Token = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey); }
            catch { /* WASM pre-render — localStorage not yet available */ }
        }

        if (string.IsNullOrWhiteSpace(Token))
            return Unauthenticated();

        var claims = ParseClaims(Token);
        if (claims is null)
        {
            Token = null;
            return Unauthenticated();
        }

        // Build the identity. The roleClaimType must match what we store in the JWT.
        // JwtSecurityTokenHandler writes ClaimTypes.Role as the short alias "role",
        // so we pass "role" here so that ClaimsPrincipal.IsInRole() works correctly.
        // nameType/roleType must match the short names written into the JWT
        var identity = new ClaimsIdentity(claims, "jwt",
            nameType: "name",
            roleType: "role");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task SetTokenAsync(string token)
    {
        Token = token;
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task ClearTokenAsync()
    {
        Token = null;
        await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        NotifyAuthenticationStateChanged(Task.FromResult(Unauthenticated()));
    }

    private static AuthenticationState Unauthenticated()
        => new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static IEnumerable<Claim>? ParseClaims(string token)
    {
        try
        {
            // Disable the default claim-type remapping so we get the raw JWT
            // claim names (e.g. "role" stays "role", not the long ClaimTypes.Role URI).
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false
            };
            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow) return null;
            return jwt.Claims;
        }
        catch { return null; }
    }
}
