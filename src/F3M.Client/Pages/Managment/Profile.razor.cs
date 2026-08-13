using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using System.Net.Http.Json;

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
            profile = await Http.GetFromJsonAsync<ProfileDto>(Endpoints.Profile.Base);
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
            myMods = await Http.GetFromJsonAsync<List<Mod>>(Endpoints.Profile.MyMods);
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

        var response = await Http.PostAsJsonAsync(Endpoints.Profile.ChangePassword, passwordDto);
        changingPassword = false;

        if (response.IsSuccessStatusCode)
        {
            passwordSuccess = "Password updated.";
            passwordDto.CurrentPassword = string.Empty;
            passwordDto.NewPassword = string.Empty;
            passwordDto.ConfirmPassword = string.Empty;
            return;
        }

        try
        {
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            passwordError = body != null && body.TryGetValue("error", out var err)
                ? err.ToString()
                : "Failed to change password.";
        }
        catch (Exception)
        {
            passwordError = "Failed to change password.";
        }
    }
}
