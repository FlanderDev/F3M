using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Web;
using System.Net.Http.Json;

namespace F3M.Client.Pages.Authentication;

public partial class LinkAccount
{
    private enum Step { EnterUrl, AwaitingReply }

    private LinkF95StartResponse dto = new();
    private string profileUrl = string.Empty;
    private string password = string.Empty;
    private string confirmPassword = string.Empty;

    private bool loading;
    private string? error;

    private bool PasswordMismatch => !string.IsNullOrEmpty(confirmPassword) && password != confirmPassword;

    private async Task OnConfirmKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await HandleStart();
    }

    private async Task HandleStart()
    {
        error = null;

        if (string.IsNullOrWhiteSpace(profileUrl))
        {
            error = "Please enter your F95zone profile URL.";
            return;
        }

        if (password.Length < 8)
        {
            error = "Password must be at least 8 characters.";
            return;
        }

        if (password != confirmPassword)
        {
            error = "Passwords do not match.";
            return;
        }

        loading = true;
        var startResponse = await Http.PostAsJsonAsync(Endpoints.F95Link.Start, new LinkF95StartRequest { ProfileUrl = profileUrl.Trim() });
        var result = await startResponse.Content.ReadFromJsonAsync<LinkF95StartResponse>()
                     ?? new LinkF95StartResponse { Success = false, Error = "Unexpected response from server." };
        loading = false;

        if (!result.Success)
        {
            error = result.Error ?? "Failed to start verification.";
            return;
        }

        dto = result;
    }

    private async Task HandleCheck()
    {
        error = null;
        loading = true;

        if (string.IsNullOrWhiteSpace(dto.F95UserId))
            return;

        var checkResponse = await Http.PostAsJsonAsync(Endpoints.F95Link.Check(dto.F95UserId), new LinkF95PollRequest { Password = password });
        var result = await checkResponse.Content.ReadFromJsonAsync<LinkF95PollResponse>()
                     ?? new LinkF95PollResponse { Status = VerificationState.Error, Message = "Unexpected response from server." };
        loading = false;

        switch (result.Status)
        {
            case VerificationState.Verified:
                // The server just signed us in via the auth cookie — force a full reload so
                // CascadingAuthenticationState re-fetches from the server with that cookie,
                // rather than relying on NotifyAuthenticationStateChanged (which only the
                // CookieAuthenticationStateProvider's own Login/Register paths trigger).
                Nav.NavigateTo("/", forceLoad: true);
                break;

            case VerificationState.Pending:
                error = result.Message
                    ?? "Code not found yet — make sure you replied to the bot's post.";
                break;

            case VerificationState.Expired:
                error = "Verification expired. Please start over.";
                dto = new();
                break;

            default:
                error = result.Message ?? "Something went wrong. Please try again.";
                break;
        }
    }

    private void ResetToStart()
    {
        dto = new();
        error = null;
        profileUrl = string.Empty;
        password = string.Empty;
        confirmPassword = string.Empty;
    }
}
