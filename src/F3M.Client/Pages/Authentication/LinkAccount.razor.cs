using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

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

    private string finalUsername = string.Empty;
    private bool isNewUser;

    private string parsedName = string.Empty;

    private bool UrlParsed => F95Profile.TryParse(profileUrl, out parsedName, out _);
    private bool ShowUrlInvalid => !string.IsNullOrWhiteSpace(profileUrl) && !UrlParsed;
    /// <summary>The server refused this name as taken; the live preview must not promise it.</summary>
    private bool NameRefused => refusedName is not null && UrlParsed && string.Equals(parsedName, refusedName, StringComparison.OrdinalIgnoreCase);
    private bool PasswordMismatch => !string.IsNullOrEmpty(confirmPassword) && password != confirmPassword;
    private bool CanStart => UrlParsed && password.Length >= 8 && password == confirmPassword;

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
                    finalUsername = result.Username ?? dto.Username ?? string.Empty;
                    isNewUser = result.IsNewUser;
                    password = confirmPassword = string.Empty;
                    step = Step.Done;

                    // The server signed us in with a cookie during the check; show that everywhere right away.
                    Acct.RefreshAuthenticationState();
                    break;

                case VerificationState.Pending:
                    pendingMessage = "No post with the code yet. Check that you posted it on your own profile and that it contains the whole code, then try again. It can take a minute to show up.";
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
