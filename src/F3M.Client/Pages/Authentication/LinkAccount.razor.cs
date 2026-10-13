using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Net;

namespace F3M.Client.Pages.Authentication;

public partial class LinkAccount
{
    private enum Step { Details, Verify, Done }

    private Step step = Step.Details;
    private LinkF95StartResponse dto = new();
    private string profileUrl = string.Empty;
    private string password = string.Empty;
    private string confirmPassword = string.Empty;

    private bool loading;
    private bool copied;
    private string? error;
    private string? pendingMessage;
    private bool usernameTaken;
    private string? refusedName;

    private string parsedName = string.Empty;

    private bool UrlParsed => F95Profile.TryParse(profileUrl, out parsedName, out _);
    private bool ShowUrlInvalid => !string.IsNullOrWhiteSpace(profileUrl) && !UrlParsed;
    /// <summary>The server refused this name as taken; the live preview must not promise it.</summary>
    private bool NameRefused => refusedName is not null && UrlParsed && string.Equals(parsedName, refusedName, StringComparison.OrdinalIgnoreCase);
    private bool PasswordMismatch => !string.IsNullOrEmpty(confirmPassword) && password != confirmPassword;
    private bool PasswordLongEnough => password.Length >= Configuration.PasswordMinLength;
    private bool PasswordTooLong => password.Length > Configuration.PasswordMaxLength;
    private bool CanStart => UrlParsed && PasswordLongEnough && !PasswordTooLong && password == confirmPassword;

    private string StepClass(Step s) => s == step ? "active" : s < step ? "done" : string.Empty;

    private async Task OnConfirmKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && CanStart)
            await HandleStart();
    }

    private async Task HandleStart()
    {
        error = null;
        usernameTaken = false;
        if (!CanStart)
            return;

        loading = true;
        try
        {
            var result = await F95Link.Start(new LinkF95StartRequest { ProfileUrl = profileUrl.Trim() });
            if (!result.Success)
            {
                error = result.Error ?? "The verification could not be started. Please try again.";
                usernameTaken = result.UsernameTaken;
                refusedName = result.UsernameTaken ? result.F95Username : null;
                return;
            }

            dto = result;
            copied = false;
            pendingMessage = null;
            step = Step.Verify;
        }
        catch (Exception)
        {
            error = "The server could not be reached. Please try again in a moment.";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task HandleCheck()
    {
        if (string.IsNullOrWhiteSpace(dto.F95UserId) || string.IsNullOrWhiteSpace(dto.ClientToken))
            return;

        error = null;
        loading = true;
        try
        {
            var result = await F95Link.Check(dto.F95UserId, new LinkF95PollRequest { Password = password, ClientToken = dto.ClientToken });
            pendingMessage = null;

            switch (result.Status)
            {
                case VerificationState.Verified:
                    password = confirmPassword = string.Empty;

                    // The server signed us in with a cookie during the check; show that everywhere right away. The
                    // profile page is [Authorize]d and waits for this refreshed state, then shows a welcome note.
                    Acct.RefreshAuthenticationState();
                    Navigation.NavigateTo($"/profile?welcome={(result.IsNewUser ? "new" : "back")}");
                    break;

                case VerificationState.Pending:
                    // The server says why when it knows (posted from another account, profile not readable, ...).
                    pendingMessage = result.Message
                                     ?? "No post with the code yet. Check that you posted it on your own profile and that it contains the whole code, then try again. It can take a minute to show up.";
                    break;

                case VerificationState.Expired:
                case VerificationState.NotFound:
                    ResetToStart();
                    error = result.Message ?? "This verification is no longer active. Please start again.";
                    break;

                default:
                    error = result.Message ?? "Something went wrong. Please try again.";
                    break;
            }
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Only the password and this browser's token are sent, and the token comes from the server.
            error = $"The server didn't accept the password. It must be {Configuration.PasswordMinLength} to {Configuration.PasswordMaxLength} characters long.";
        }
        catch (Exception)
        {
            error = "The server could not be reached. Please try again in a moment.";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task CopyCode()
    {
        copied = await JS.InvokeAsync<bool>("f3m.copyText", dto.VerificationGuid);
    }

    private void ResetToStart()
    {
        step = Step.Details;
        dto = new();
        error = null;
        pendingMessage = null;
        copied = false;
    }
}
