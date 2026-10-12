using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated SetupApiControllerBase (routes and the admin-only rule come from
/// <see cref="ISetupApi"/>). SetupService does the work; this maps its <see cref="InvalidOperationException"/>s
/// (invalid input) to 400 with the message.
/// </summary>
public sealed class SetupController(ISetupApi api) : SetupApiControllerBase
{
    public override async Task<ActionResult<SetupStatusDto>> GetStatusAsync(CancellationToken ct)
        => Ok(await api.GetStatusAsync(ct));

    public override Task<ActionResult<BotSettingsDto>> SaveBotAsync(BotSettingsUpdateDto dto, CancellationToken ct)
        => Run(() => api.SaveBotAsync(dto, ct));

    public override Task<ActionResult<BotSettingsDto>> RemoveBotAsync(CancellationToken ct)
        => Run(() => api.RemoveBotAsync(ct));

    public override Task<ActionResult<BotTestResultDto>> TestBotAsync(CancellationToken ct)
        => Run(() => api.TestBotAsync(ct));

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
