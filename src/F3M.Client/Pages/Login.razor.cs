using F3M.Shared.Models;

namespace F3M.Client.Pages;

public partial class Login
{
    private LoginDto dto = new();
    private string? error;
    private bool loading;

    private async Task HandleLogin()
    {
        loading = true;
        error = null;
        var result = await Auth.LoginAsync(dto);
        loading = false;
        if (result.Success)
            Nav.NavigateTo("/", forceLoad: false);
        else
            error = result.Error ?? "Login failed.";
    }
}