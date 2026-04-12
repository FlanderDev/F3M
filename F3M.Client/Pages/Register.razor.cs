using F3M.Shared.Models;

namespace F3M.Client.Pages;

public partial class Register
{
    private RegisterDto dto = new();
    private string? error;
    private bool loading;

    private async Task HandleRegister()
    {
        loading = true;
        error = null;
        var result = await Auth.RegisterAsync(dto);
        loading = false;
        if (result.Success)
            Nav.NavigateTo("/");
        else
            error = result.Error ?? "Registration failed.";
    }
}