using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated ImportApiControllerBase (routes and the admin-only rule come from
/// <see cref="IImportApi"/>). F95ImportService does the work; this maps its <see cref="InvalidOperationException"/>s
/// (a step is running, the plan is unusable) to 400 with the message.
/// </summary>
public sealed class ImportAdminController(IImportApi api) : ImportApiControllerBase
{
    public override async Task<ActionResult<ImportStatusDto>> GetStatusAsync(CancellationToken ct)
        => Ok(await api.GetStatusAsync(ct));

    public override Task<ActionResult<ImportJobDto>> StartDownloadAsync(CancellationToken ct)
        => Run(() => api.StartDownloadAsync(ct));

    public override Task<ActionResult<ImportJobDto>> StartImportAsync(ImportStartDto dto, CancellationToken ct)
        => Run(() => api.StartImportAsync(dto, ct));

    public override async Task<IActionResult> CancelAsync(CancellationToken ct)
    {
        await api.CancelAsync(ct);
        return NoContent();
    }

    public override Task<ActionResult<ImportPlanJsonDto>> GetPlanAsync(CancellationToken ct)
        => Run(() => api.GetPlanAsync(ct));

    public override Task<ActionResult<ImportPlanInfoDto>> ReplacePlanAsync(ImportPlanJsonDto dto, CancellationToken ct)
        => Run(() => api.ReplacePlanAsync(dto, ct));

    public override Task<ActionResult<ImportPlanInfoDto>> ResetPlanAsync(CancellationToken ct)
        => Run(() => api.ResetPlanAsync(ct));

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
