using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for client-side error telemetry. The RouteGen generators derive both the
/// server's abstract controller base (TelemetryApiControllerBase) and the client's HttpClient
/// implementation (HttpTelemetryApi) from this interface — this is the only hand-written piece.
/// </summary>
[ApiRoute("api/telemetry", HttpClientName = Configuration.AppName)]
public partial interface ITelemetryApi
{
    [Post("error")]
    Task ReportError([Body] Telemetry.ErrorReport errorReport, CancellationToken ct = default);

    /// <summary>Counts a page load (and the visitor, once per day) for the statistics page.</summary>
    [Post("visit")]
    Task ReportVisit([Body] VisitReport visit, CancellationToken ct = default);

    /// <summary>Counts a search, and whether it found anything, for the statistics page.</summary>
    [Post("search")]
    Task ReportSearch([Body] SearchReport search, CancellationToken ct = default);
}
