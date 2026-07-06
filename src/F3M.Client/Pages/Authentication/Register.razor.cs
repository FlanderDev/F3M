using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Web;

namespace F3M.Client.Pages.Authentication;

public partial class Register
{
    private enum Step { EnterUrl, AwaitingReply }

    private LinkF95StartResponse LinkF95StartResponse = new();
    private Step   step           = Step.EnterUrl;
    private string profileUrl     = string.Empty;
    private string password       = string.Empty;
    private string confirmPassword = string.Empty;

    private bool    loading;
    private string? error;

    private bool passwordMismatch =>
        !string.IsNullOrEmpty(confirmPassword) && password != confirmPassword;

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
        var result = await Auth.LinkF95StartAsync(profileUrl.Trim());
        loading = false;

        if (!result.Success)
        {
            error = result.Error ?? "Failed to start verification.";
            return;
        }

        LinkF95StartResponse = result;
        step = Step.AwaitingReply;
    }

    private async Task HandleCheck()
    {
        error   = null;
        loading = true;

        if (string.IsNullOrWhiteSpace(LinkF95StartResponse.F95UserId))
            return;

        var result = await Auth.LinkF95PollAsync(LinkF95StartResponse.F95UserId, password);
        loading = false;

        switch (result.Status)
        {
            case "Verified":
                Nav.NavigateTo("/", forceLoad: false);
                break;

            case "Pending":
                error = result.Message
                    ?? "Code not found yet — make sure you replied to the bot's post.";
                break;

            case "Expired":
                error = "Verification expired. Please start over.";
                step  = Step.EnterUrl;
                break;

            default:
                error = result.Message ?? "Something went wrong. Please try again.";
                break;
        }
    }

    private void ResetToStart()
    {
        step              = Step.EnterUrl;
        LinkF95StartResponse = new();
        error             = null;
        profileUrl        = string.Empty;
        password          = string.Empty;
        confirmPassword   = string.Empty;
    }
}
