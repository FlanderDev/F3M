using F3M.Shared;
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
}
