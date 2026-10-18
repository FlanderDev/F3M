using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>Thin controller over the RouteGen-generated ServerUpdateApiControllerBase (admins only); ServerUpdateService does the work.</summary>
public sealed class ServerUpdateController(IServerUpdateApi updates) : ServerUpdateApiControllerBase
{
    public override async Task<ActionResult<ServerUpdateStatus>> GetStatusAsync(CancellationToken ct)
        => Ok(await updates.GetStatusAsync(ct));

    public override async Task<ActionResult<ServerUpdateStatus>> RequestCheckAsync(CancellationToken ct)
    {
        try
        {
            return Ok(await updates.RequestCheckAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    public override async Task<ActionResult<ServerUpdateStatus>> RequestUpdateAsync(CancellationToken ct)
    {
        try
        {
            return Ok(await updates.RequestUpdateAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }
}
