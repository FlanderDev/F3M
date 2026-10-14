using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>Wire contract for the admin statistics page. RouteGen generates StatsApiControllerBase and HttpStatsApi from it.</summary>
[ApiRoute("api/admin/stats", HttpClientName = Configuration.AppName)]
[GenAuthorize(Roles = AppRoles.Admin)]
public interface IStatsApi
{
    /// <summary>The site's statistics for the last <paramref name="days"/> days (1 to <see cref="MaxDays"/>), ending today (UTC).</summary>
    [Get("overview")]
    Task<SiteStats> GetStatsAsync([Query] int days = 30, CancellationToken ct = default);

    public const int MaxDays = 365;

    /// <summary>Counted downloads, visits, searches and sign-ins older than this are removed.</summary>
    public const int RetentionDays = 730;
}
