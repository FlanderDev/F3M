using F3M.Server.Data;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

[Route("api/[controller]")]
[Route(Endpoints.Tele.Base)]
[ApiController]
public sealed class TelemetryController(AppDbContext db, ILogger<ModsController> logger) : ControllerBase
{
    [HttpPost(Endpoints.Error)]
    public async Task<IActionResult> PostErrorAsync([FromBody] Telemetry.ErrorReport errorReport)
    {
        try
        {
            await db.TelemetryErrorReports.AddAsync(errorReport);
            await db.SaveChangesAsync();
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while posting the error report.");
            return Problem("Unable to post error report.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
