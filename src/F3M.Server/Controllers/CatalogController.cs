using F3M.Server.Services;
using F3M.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Admin maintenance for the public catalog. A rebuild also fills in placements for files uploaded before catalog
/// support, so run it once after deploying this change.
/// </summary>
[ApiController]
[Route("api/catalog")]
public sealed class CatalogController(CatalogService catalog) : ControllerBase
{
    [HttpPost("rebuild")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<CatalogRebuildResult>> Rebuild(CancellationToken ct) =>
        Ok(await catalog.RebuildAllAsync(ct));
}
