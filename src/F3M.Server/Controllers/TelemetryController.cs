using F3M.Server.Data;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

public sealed class TelemetryController(AppDbContext db, ILogger<TelemetryController> logger) : TelemetryApiControllerBase
{
    // Anyone can post here, so only bounded text is stored, and the id and time are the server's.
    private const int MaxMessage = 2_000;
    private const int MaxStackTrace = 16_000;
    private const int MaxShort = 500;

    public override async Task<IActionResult> ReportError(Telemetry.ErrorReport errorReport, CancellationToken ct)
    {
        try
        {
            var report = new Telemetry.ErrorReport
            {
                Message = Truncate(errorReport.Message, MaxMessage),
                StackTrace = Truncate(errorReport.StackTrace, MaxStackTrace),
                Source = Truncate(errorReport.Source, MaxShort),
                TargetSite = Truncate(errorReport.TargetSite, MaxShort),
                Timestamp = DateTime.UtcNow,
            };
            await db.TelemetryErrorReports.AddAsync(report, ct);
            await db.SaveChangesAsync(ct);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while posting the error report.");
            return Problem("Unable to post error report.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string? Truncate(string? text, int max) => text is { Length: > 0 } && text.Length > max ? text[..max] : text;
}
