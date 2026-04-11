using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace F3M.Client.Services;

public class AuthService(HttpClient http, AuthenticationStateProvider authProvider)
{
    public async Task<AuthResult> RegisterAsync(RegisterDto dto)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/register", dto);
            var result   = await response.Content.ReadFromJsonAsync<AuthResult>();
            if (result?.Success == true && result.Token is not null)
                await ((F3MAuthStateProvider)authProvider).SetTokenAsync(result.Token);
            return result ?? new AuthResult { Success = false, Error = "Unknown error." };
        }
        catch (Exception ex)
        {
            return new AuthResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<AuthResult> LoginAsync(LoginDto dto)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/auth/login", dto);
            var result   = await response.Content.ReadFromJsonAsync<AuthResult>();
            if (result?.Success == true && result.Token is not null)
                await ((F3MAuthStateProvider)authProvider).SetTokenAsync(result.Token);
            return result ?? new AuthResult { Success = false, Error = "Unknown error." };
        }
        catch (Exception ex)
        {
            return new AuthResult { Success = false, Error = ex.Message };
        }
    }

    public async Task LogoutAsync()
        => await ((F3MAuthStateProvider)authProvider).ClearTokenAsync();

    public string? GetToken()
        => ((F3MAuthStateProvider)authProvider).Token;

    public bool IsAdmin
        => ((F3MAuthStateProvider)authProvider).IsAdmin;
}
