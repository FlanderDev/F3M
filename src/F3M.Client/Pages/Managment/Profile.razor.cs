using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Pages.Managment;

public partial class Profile
{
    private ProfileDto? profile;
    private List<Mod>? myMods;
    private string? loadError;
    private bool loading = true;

    private readonly ChangePasswordDto passwordDto = new();
    private bool changingPassword;
    private string? passwordError;
    private string? passwordSuccess;

    // Set by the account-link page after a successful verification: "new" (account created) or "back" (signed in).
    private const string WelcomeNew = "new";
    private const string WelcomeBack = "back";

    [SupplyParameterFromQuery(Name = "welcome")]
    public string? WelcomeQuery { get; set; }

    private string? welcome;

    protected override void OnParametersSet()
    {
        welcome = WelcomeQuery is WelcomeNew or WelcomeBack ? WelcomeQuery : null;
    }

    /// <summary>Hides the note and drops it from the address, so a reload or a bookmark doesn't bring it back.</summary>
    private void DismissWelcome()
    {
        welcome = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("welcome", (string?)null), replace: true);
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadProfileAsync();
        await LoadMyModsAsync();
    }

    private async Task LoadProfileAsync()
    {
        loading = true;
        loadError = null;
        try
        {
            profile = await Api.GetProfileAsync();
        }
        catch (Exception)
        {
            loadError = "Failed to load your profile. Please try refreshing the page.";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task LoadMyModsAsync()
    {
        try
        {
            myMods = await Api.GetMyModsAsync();
        }
        catch (Exception)
        {
            // Non-critical — the rest of the profile page still works without this.
            myMods = [];
        }
    }

    private string F95ProfileUrl =>
        profile?.F95Username is not null && profile.F95UserId is not null
            ? $"https://f95zone.to/members/{profile.F95Username}.{profile.F95UserId}/"
            : string.Empty;

    private async Task HandleChangePassword()
    {
        changingPassword = true;
        passwordError = null;
        passwordSuccess = null;

        var result = await Api.ChangePasswordAsync(passwordDto);
        changingPassword = false;

        if (result.Success)
        {
            passwordSuccess = "Password updated.";
            passwordDto.CurrentPassword = string.Empty;
            passwordDto.NewPassword = string.Empty;
            passwordDto.ConfirmPassword = string.Empty;
            return;
        }

        passwordError = result.Error ?? "Failed to change password.";
    }
}
