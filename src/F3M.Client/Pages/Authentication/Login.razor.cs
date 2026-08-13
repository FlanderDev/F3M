using F3M.Client.Identity.Models;
using F3M.Shared.Models;

namespace F3M.Client.Pages.Authentication;

public partial class Login
{
    private readonly LoginDto dto = new();
    private FormResult formResult = new();

    private bool loading;

    private async Task HandleLogin()
    {
        loading = true;
        formResult = await Acct.LoginAsync(dto.UsernameOrEmail, dto.Password);
        loading = false;

        if (formResult.Succeeded)
            Navigation.NavigateTo("/", forceLoad: false);
    }
}