using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>Thin controller over the RouteGen-generated StatsApiControllerBase (admins only, from IStatsApi); StatsService does the work.</summary>
public sealed class StatsController(IStatsApi statsApi) : StatsApiControllerBase
{
    public override async Task<ActionResult<SiteStats>> GetStatsAsync(int days, CancellationToken ct)
        => Ok(await statsApi.GetStatsAsync(days, ct));
}
