using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// The admin page that downloads the community mods from F95zone and imports them (import-plan.json). Each step only
/// starts when an admin asks for it, and runs on the server in the background; the page polls <see cref="GetStatusAsync"/>.
/// Starting a step while another runs, or with an unusable plan, throws <see cref="InvalidOperationException"/>.
/// </summary>
[ApiRoute("api/admin/import", HttpClientName = Configuration.AppName)]
[GenAuthorize(Roles = AppRoles.Admin)]
public interface IImportApi
{
    [Get("status")]
    Task<ImportStatusDto> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Downloads the files of the plan that are not on the server yet.</summary>
    [Post("download")]
    Task<ImportJobDto> StartDownloadAsync(CancellationToken ct = default);

    /// <summary>Imports the downloaded mods that are not (fully) on the site yet, dependencies first.</summary>
    [Post("run")]
    Task<ImportJobDto> StartImportAsync([Body] ImportStartDto dto, CancellationToken ct = default);

    /// <summary>
    /// Downloads and imports only the frameworks of the plan: the mods that mod types need
    /// (<see cref="Helpers.ModTemplate.RequiredModName"/>), with what they depend on. One step, for a new site.
    /// </summary>
    [Post("frameworks")]
    Task<ImportJobDto> StartFrameworksAsync(CancellationToken ct = default);

    /// <summary>Stops the running step after the file or mod it is working on.</summary>
    [Post("cancel")]
    Task CancelAsync(CancellationToken ct = default);

    /// <summary>The JSON of the plan in use.</summary>
    [Get("plan")]
    Task<ImportPlanJsonDto> GetPlanAsync(CancellationToken ct = default);

    /// <summary>Replaces the plan with an uploaded one, after checking it.</summary>
    [Put("plan")]
    Task<ImportPlanInfoDto> ReplacePlanAsync([Body] ImportPlanJsonDto dto, CancellationToken ct = default);

    /// <summary>Goes back to the plan shipped with this build.</summary>
    [Delete("plan")]
    Task<ImportPlanInfoDto> ResetPlanAsync(CancellationToken ct = default);
}
