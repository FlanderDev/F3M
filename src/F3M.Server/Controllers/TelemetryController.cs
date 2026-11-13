using F3M.Server.Logging;
using F3M.Server.Statistics;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

public sealed class TelemetryController(LogStore log, StatsRecorder stats) : TelemetryApiControllerBase
{
    // Anyone can post here: LogStore bounds the text, and the time is the server's. What scripts and bots post is marked
    // as theirs, so the statistics can leave it out.
    public override Task<IActionResult> ReportError(Telemetry.ErrorReport errorReport, CancellationToken ct)
    {
        var client = RequestClients.Of(Request);
        var details = string.Join(Environment.NewLine, new[]
        {
            errorReport.TargetSite is { Length: > 0 } method ? $"Method: {method}" : null,
            errorReport.StackTrace
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        log.Write(new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = LogSeverity.Error,
            Source = LogSource.Browser,
            Category = errorReport.Source,
            Message = string.IsNullOrWhiteSpace(errorReport.Message) ? "(no message)" : errorReport.Message,
            Details = details.Length > 0 ? details : null,
            UserName = User.Identity is { IsAuthenticated: true } identity ? identity.Name : null,
            // The page the visitor was on, as far as the browser says: the referrer, without its query.
            RequestPath = Request.Headers.Referer.ToString() is { Length: > 0 } referer && Uri.TryCreate(referer, UriKind.Absolute, out var page)
                ? page.AbsolutePath
                : null,
            Client = client,
            UserAgent = client == RequestClient.Other ? Request.Headers.UserAgent.ToString() : null
        });

        return Task.FromResult<IActionResult>(Ok());
    }

    // Anyone can post these too: StatsRecorder caps how many different visitors and search terms it keeps a day. Only
    // the website sends them, so whatever else posts them isn't counted.
    public override Task<IActionResult> ReportVisit(VisitReport visit, CancellationToken ct)
    {
        if (RequestClients.Of(Request) != RequestClient.Website)
            return Task.FromResult<IActionResult>(Ok());

        stats.Count(StatsRecorder.Metrics.PageLoad);
        if (visit.VisitorId is { Length: > 0 } visitorId)
            stats.Visitor(visitorId);
        return Task.FromResult<IActionResult>(Ok());
    }

    public override Task<IActionResult> ReportSearch(SearchReport search, CancellationToken ct)
    {
        if (RequestClients.Of(Request) == RequestClient.Website && StatsRecorder.NormalizeKey(search.Term) is { Length: > 0 } term)
        {
            stats.Count(StatsRecorder.Metrics.Search, term);
            if (search.Results == 0)
                stats.Count(StatsRecorder.Metrics.SearchWithoutResults, term);
        }
        return Task.FromResult<IActionResult>(Ok());
    }
}
