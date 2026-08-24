using F3M.Server.Data;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

// Thin controller over the RouteGen-generated TelemetryApiControllerBase — no route attributes,
// no route strings, anywhere. Routing/binding comes entirely from the generated base (see
// obj/**/generated/RouteGen.Generators/.../F3M.Shared.Api_ITelemetryApi.g.cs after build).
public sealed class TelemetryController(AppDbContext db, ILogger<TelemetryController> logger) : TelemetryApiControllerBase
{
    public override async Task<IActionResult> ReportError(Telemetry.ErrorReport errorReport, CancellationToken ct)
    {
        try
        {
            await db.TelemetryErrorReports.AddAsync(errorReport, ct);
            await db.SaveChangesAsync(ct);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while posting the error report.");
            return Problem("Unable to post error report.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
