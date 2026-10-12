using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// The setup page: a checklist of what a new site still needs, and the F95zone bot account that verifies account
/// links. Invalid input throws <see cref="InvalidOperationException"/> with the reason.
/// </summary>
[ApiRoute("api/admin/setup", HttpClientName = Configuration.AppName)]
[GenAuthorize(Roles = AppRoles.Admin)]
public interface ISetupApi
{
    [Get("status")]
    Task<SetupStatusDto> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Saves the bot account on the site. It replaces the F95Bot__* settings until it is removed.</summary>
    [Put("bot")]
    Task<BotSettingsDto> SaveBotAsync([Body] BotSettingsUpdateDto dto, CancellationToken ct = default);

    /// <summary>Removes the saved bot account; the F95Bot__* settings apply again.</summary>
    [Delete("bot")]
    Task<BotSettingsDto> RemoveBotAsync(CancellationToken ct = default);

    /// <summary>Signs in to F95zone with the bot account in use.</summary>
    [Post("bot/test")]
    Task<BotTestResultDto> TestBotAsync(CancellationToken ct = default);
}
