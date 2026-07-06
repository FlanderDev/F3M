using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

namespace F3M.Client.Services;

public class AuthService(HttpClient http, AuthenticationStateProvider authProvider)
{
    // ── Password login (kept for admin / legacy accounts) ─────────────────────

    public async Task<AuthResult> LoginAsync(LoginDto dto)
    {
        try
        {
            var response = await http.PostAsJsonAsync(Endpoints.Auth.Login, dto);
            var result = await response.Content.ReadFromJsonAsync<AuthResult>();
            if (result?.Success == true && result.Token is not null)
                await ((F3MAuthStateProvider)authProvider).SetTokenAsync(result.Token);
            return result ?? new AuthResult { Success = false, Error = "Unknown error." };
        }
        catch (Exception ex)
        {
            return new AuthResult { Success = false, Error = ex.Message };
        }
    }

    // ── F95zone account linking ────────────────────────────────────────────────

    /// <summary>
    /// Sends the F95zone profile URL to the server, which posts the GUID challenge
    /// on the bot's profile wall. Returns the GUID and instructions on success.
    /// </summary>
    public async Task<LinkF95StartResponse> LinkF95StartAsync(string profileUrl)
    {
        try
        {
            var url = Endpoints.F95Link.Start;
            var response = await http.PostAsJsonAsync(url, new LinkF95StartRequest { ProfileUrl = profileUrl });
            return await response.Content.ReadFromJsonAsync<LinkF95StartResponse>() ?? new LinkF95StartResponse { Success = false, Error = "Empty response from server." };
        }
        catch (Exception ex)
        {
            return new LinkF95StartResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Polls the server to check whether the user has replied with the correct GUID.
    /// On success (Status == "Verified") the JWT is stored and auth state is updated.
    /// </summary>
    public async Task<LinkF95PollResponse> LinkF95PollAsync(string f95UserId, string password)
    {
        try
        {
            var url = Endpoints.F95Link.Check(f95UserId);
            var response = await http.PostAsJsonAsync(url, new LinkF95PollRequest { Password = password });

            var result = await response.Content.ReadFromJsonAsync<LinkF95PollResponse>()
                         ?? new LinkF95PollResponse { Status = "Error", Message = "Empty response." };

            if (result.Status == "Verified" && result.Token is not null)
                await ((F3MAuthStateProvider)authProvider).SetTokenAsync(result.Token);

            return result;
        }
        catch (Exception ex)
        {
            return new LinkF95PollResponse { Status = "Error", Message = ex.Message };
        }
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    public async Task LogoutAsync() => await ((F3MAuthStateProvider)authProvider).ClearTokenAsync();
    public string? GetToken() => ((F3MAuthStateProvider)authProvider).Token;
    public bool IsAdmin => ((F3MAuthStateProvider)authProvider).IsAdmin;
}
