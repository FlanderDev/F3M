using F3M.Client.Identity.Models;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Pages.Authentication;

public partial class Login
{
    private readonly LoginDto dto = new();
    private FormResult formResult = new();

    private bool loading;

    /// <summary>Where to go after signing in, e.g. the page that asked for a login. Only local paths are followed.</summary>
    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }

    private async Task HandleLogin()
    {
        loading = true;
        formResult = await Acct.LoginAsync(dto.UsernameOrEmail.Trim(), dto.Password);
        loading = false;

        if (formResult.Succeeded)
            Navigation.NavigateTo(IsLocal(ReturnUrl) ? ReturnUrl! : "/", forceLoad: false);
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
