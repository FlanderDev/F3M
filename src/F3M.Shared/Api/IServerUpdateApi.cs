using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for updating the server from the admin page. RouteGen generates ServerUpdateApiControllerBase and
/// HttpServerUpdateApi from it. The update itself runs on the host (see admin-updates.sh); these calls only leave
/// requests for it and read what it reported.
/// </summary>
[ApiRoute("api/admin/server-update", HttpClientName = Configuration.AppName)]
[GenAuthorize(Roles = AppRoles.Admin)]
public interface IServerUpdateApi
{
    [Get("status")]
    Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Asks the host to look for new commits now. Throws <see cref="InvalidOperationException"/> when the host service isn't installed.</summary>
    [Post("check")]
    Task<ServerUpdateStatus> RequestCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks the host to update the server: backup, newest code, rebuild, restart. Does nothing more when an update is
    /// already requested or running. Throws <see cref="InvalidOperationException"/> when the host service isn't installed.
    /// </summary>
    [Post("update")]
    Task<ServerUpdateStatus> RequestUpdateAsync(CancellationToken ct = default);
}
