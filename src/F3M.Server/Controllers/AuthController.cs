using F3M.Server.Models;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

[ApiController]
[Route(Endpoints.Auth.Base)]
public class AuthController(UserManager<AppUser> userManager) : ControllerBase
{
    [HttpPost(Endpoints.Register)]
    public async Task<ActionResult<AuthResult>> Register([FromBody] RegisterDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(new AuthResult { Success = false, Error = "Invalid input." });

        if (await userManager.FindByNameAsync(dto.Username) is not null)
            return Conflict(new AuthResult { Success = false, Error = "Username is already taken." });

        if (await userManager.FindByEmailAsync(dto.Email) is not null)
            return Conflict(new AuthResult { Success = false, Error = "Email is already registered." });

        var user = new AppUser
        {
            UserName = dto.Username,
            Email = dto.Email,
            RegisteredAt = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(user, dto.Password);
        if (!createResult.Succeeded)
            return BadRequest(new AuthResult
            {
                Success = false,
                Error = string.Join(" ", createResult.Errors.Select(e => e.Description))
            });

        await userManager.AddToRoleAsync(user, AppRoles.User);

        return Ok();
    }

    [HttpPost(Endpoints.Login)]
    public async Task<ActionResult<AuthResult>> Login([FromBody] LoginDto dto)
    {
        var user = await userManager.FindByNameAsync(dto.UsernameOrEmail) ?? await userManager.FindByEmailAsync(dto.UsernameOrEmail);

        if (user is null || !await userManager.CheckPasswordAsync(user, dto.Password))
            return Unauthorized(new AuthResult { Success = false, Error = "Invalid credentials." });

        return Ok();
    }
}
