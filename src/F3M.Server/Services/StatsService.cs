using F3M.Server.Data;
using F3M.Server.Logging;
using F3M.Server.Models;
using F3M.Server.Statistics;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IStatsApi"/>: the statistics page's numbers, from the site's own data (mods,
/// users, verifications), the site log (warnings and errors) and the counted events (<see cref="StatsRecorder"/>).
/// The site's data is small enough to sum in memory, which keeps the date handling out of SQL.
/// </summary>
public sealed class StatsService(AppDbContext db, LogDbContext log, StatsDbContext stats) : IStatsApi
{
    private const int TopCount = 10;
    private const int TopSearchCount = 15;
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);

    public async Task<SiteStats> GetStatsAsync(int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, IStatsApi.MaxDays);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(days - 1));
        var previousFrom = from.AddDays(-days);
        var previousTo = from.AddDays(-1);

        var result = new SiteStats { Days = days, From = from, To = to };

        await AddSiteDataAsync(result, previousFrom, ct);
        await AddCountedEventsAsync(result, previousFrom, previousTo, ct);
        await AddProblemsAsync(result, ct);
        await AddVerificationsAsync(result, ct);
        return result;
    }

    /// <summary>Totals, growth, types and the most downloaded mods, from the mods and accounts themselves.</summary>
    private async Task AddSiteDataAsync(SiteStats result, DateOnly previousFrom, CancellationToken ct)
    {
        var versions = await db.Mods.AsNoTracking()
            .Where(m => m.IsApproved)
            .Select(m => new { m.Id, m.ModGroupId, m.Name, m.Author, m.UploadedAt, m.DownloadCount })
            .ToListAsync(ct);
        var files = await db.ModFiles.AsNoTracking().Select(f => new { f.ModId, f.FileSizeBytes }).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => new { u.RegisteredAt, Linked = u.F95UserId != null }).ToListAsync(ct);

        var bytesByVersion = files.GroupBy(f => f.ModId).ToDictionary(g => g.Key, g => g.Sum(f => f.FileSizeBytes));
        var groups = versions
            .GroupBy(v => v.ModGroupId)
            .Select(g =>
            {
                var ordered = g.OrderBy(v => v.UploadedAt).ToList();
                var latest = ordered[^1];
                return new
                {
                    GroupId = g.Key,
                    FirstUpload = ordered[0].UploadedAt,
                    FirstVersionId = ordered[0].Id,
                    Latest = latest,
                    Downloads = g.Sum(v => (long)v.DownloadCount),
                    Bytes = g.Sum(v => bytesByVersion.GetValueOrDefault(v.Id))
                };
            })
            .ToList();
        var firstVersionIds = groups.Select(g => g.FirstVersionId).ToHashSet();

        result.Totals = new StatsTotals
        {
            Mods = groups.Count,
            Versions = versions.Count,
            Files = files.Count,
            StorageBytes = files.Sum(f => f.FileSizeBytes),
            Downloads = groups.Sum(g => g.Downloads),
            Users = users.Count,
            LinkedUsers = users.Count(u => u.Linked),
            UnclaimedMods = await db.ModGroups.CountAsync(g => g.OwnerId == ModGroup.UnclaimedOwnerId, ct)
        };

        bool InRange(DateTime time, DateOnly first, DateOnly last) => DateOnly.FromDateTime(time) is var day && day >= first && day <= last;
        void FillSiteNumbers(StatsPeriod period, DateOnly first, DateOnly last)
        {
            period.NewUsers = users.Count(u => InRange(u.RegisteredAt, first, last));
            period.NewMods = groups.Count(g => InRange(g.FirstUpload, first, last));
            period.NewVersions = versions.Count(v => !firstVersionIds.Contains(v.Id) && InRange(v.UploadedAt, first, last));
        }
        FillSiteNumbers(result.Period, result.From, result.To);
        FillSiteNumbers(result.PreviousPeriod, previousFrom, result.From.AddDays(-1));

        var thisMonth = new DateOnly(result.To.Year, result.To.Month, 1);
        for (var i = 11; i >= 0; i--)
        {
            var month = thisMonth.AddMonths(-i);
            var last = month.AddMonths(1).AddDays(-1);
            result.Growth.Add(new MonthlyGrowth
            {
                Month = month,
                NewUsers = users.Count(u => InRange(u.RegisteredAt, month, last)),
                NewMods = groups.Count(g => InRange(g.FirstUpload, month, last)),
                NewVersions = versions.Count(v => !firstVersionIds.Contains(v.Id) && InRange(v.UploadedAt, month, last))
            });
        }

        // Tag suggestions per week, from the suggestions themselves (a request keeps its name after it became a tag).
        var thisWeek = result.To.AddDays(-(((int)result.To.DayOfWeek + 6) % 7));
        var firstWeek = thisWeek.AddDays(-7 * 11);
        var since = firstWeek.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var suggestions = await db.TagSuggestions.AsNoTracking()
            .Where(t => t.CreatedAt >= since || t.DecidedAt >= since)
            .Select(t => new { t.CreatedAt, t.DecidedAt, IsRequest = t.RequestedName != null })
            .ToListAsync(ct);
        for (var week = firstWeek; week <= thisWeek; week = week.AddDays(7))
        {
            var last = week.AddDays(6);
            result.TagSuggestions.Add(new WeeklyTagSuggestions
            {
                Week = week,
                Suggested = suggestions.Count(t => !t.IsRequest && InRange(t.CreatedAt, week, last)),
                Requested = suggestions.Count(t => t.IsRequest && InRange(t.CreatedAt, week, last)),
                Decided = suggestions.Count(t => t.DecidedAt is { } at && InRange(at, week, last))
            });
        }

        // By type tag; a mod of several types counts for each of them.
        var typesByGroup = (await (
                    from link in db.ModGroupTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                    where tag.TypeId != null
                    select new { link.ModGroupId, tag.Name })
                .ToListAsync(ct))
            .GroupBy(t => t.ModGroupId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList());
        result.Categories = groups
            .SelectMany(g => (typesByGroup.GetValueOrDefault(g.GroupId) ?? ["(no type)"]).Select(type => (Type: type, Group: g)))
            .GroupBy(t => t.Type)
            .Select(c => new CategoryStats { Name = c.Key, Mods = c.Count(), Downloads = c.Sum(t => t.Group.Downloads), StorageBytes = c.Sum(t => t.Group.Bytes) })
            .OrderByDescending(c => c.Mods).ThenBy(c => c.Name)
            .ToList();

        result.TopModsAllTime = groups
            .Where(g => g.Downloads > 0)
            .OrderByDescending(g => g.Downloads).ThenBy(g => g.Latest.Name)
            .Take(TopCount)
            .Select(g => new TopMod { GroupId = g.GroupId, ModId = g.Latest.Id, Name = g.Latest.Name, Author = g.Latest.Author, Downloads = g.Downloads })
            .ToList();

        // For the period's top list, which only knows group ids.
        modsByGroup = groups.ToDictionary(g => g.GroupId, g => (g.Latest.Id, g.Latest.Name, g.Latest.Author));
    }

    private Dictionary<int, (int ModId, string Name, string Author)> modsByGroup = [];

    /// <summary>Downloads, visits, searches, sign-ins and desktop syncs, which only exist as counted events.</summary>
    private async Task AddCountedEventsAsync(SiteStats result, DateOnly previousFrom, DateOnly previousTo, CancellationToken ct)
    {
        var firstKey = StatsRecorder.DayKey(previousFrom);
        var lastKey = StatsRecorder.DayKey(result.To);
        var fromKey = StatsRecorder.DayKey(result.From);
        var previousToKey = StatsRecorder.DayKey(previousTo);

        var counts = await stats.Counts.AsNoTracking()
            .Where(c => string.Compare(c.Day, firstKey) >= 0 && string.Compare(c.Day, lastKey) <= 0)
            .ToListAsync(ct);
        var visitors = await stats.Visitors.AsNoTracking()
            .Where(v => string.Compare(v.Day, firstKey) >= 0 && string.Compare(v.Day, lastKey) <= 0)
            .ToListAsync(ct);

        var since = await stats.Counts.MinAsync(c => (string?)c.Day, ct);
        result.CountingSince = since is null ? null : StatsRecorder.ParseDay(since);

        var current = counts.Where(c => string.Compare(c.Day, fromKey) >= 0).ToList();
        var previous = counts.Where(c => string.Compare(c.Day, previousToKey) <= 0).ToList();
        var currentVisitors = visitors.Where(v => string.Compare(v.Day, fromKey) >= 0).ToList();
        var previousVisitors = visitors.Where(v => string.Compare(v.Day, previousToKey) <= 0).ToList();

        static long Sum(IEnumerable<DailyCount> rows, params string[] metrics) => rows.Where(r => metrics.Contains(r.Metric)).Sum(r => r.Count);
        void FillCounted(StatsPeriod period, List<DailyCount> rows, List<DailyVisitor> visits)
        {
            period.Downloads = Sum(rows, StatsRecorder.Metrics.DownloadBrowser, StatsRecorder.Metrics.DownloadDesktop);
            period.PageLoads = Sum(rows, StatsRecorder.Metrics.PageLoad);
            period.DesktopSyncs = Sum(rows, StatsRecorder.Metrics.DesktopSync);
            period.SignIns = Sum(rows, StatsRecorder.Metrics.SignInPassword, StatsRecorder.Metrics.SignInF95);
            period.Visitors = visits.Select(v => v.Visitor).Distinct().Count();
        }
        FillCounted(result.Period, current, currentVisitors);
        FillCounted(result.PreviousPeriod, previous, previousVisitors);

        var byDay = current.ToLookup(c => c.Day);
        var visitorsByDay = currentVisitors.GroupBy(v => v.Day).ToDictionary(g => g.Key, g => g.Count());
        foreach (var day in EachDay(result.From, result.To))
        {
            var key = StatsRecorder.DayKey(day);
            var rows = byDay[key].ToList();
            result.Downloads.Add(new DailyDownloads
            {
                Day = day,
                Browser = Sum(rows, StatsRecorder.Metrics.DownloadBrowser),
                Desktop = Sum(rows, StatsRecorder.Metrics.DownloadDesktop)
            });
            result.Visits.Add(new DailyVisits { Day = day, Visitors = visitorsByDay.GetValueOrDefault(key), PageLoads = Sum(rows, StatsRecorder.Metrics.PageLoad) });
            result.SignIns.Add(new DailySignIns
            {
                Day = day,
                Password = Sum(rows, StatsRecorder.Metrics.SignInPassword),
                F95 = Sum(rows, StatsRecorder.Metrics.SignInF95)
            });
        }

        result.TopModsInPeriod = current
            .Where(c => c.Metric is StatsRecorder.Metrics.DownloadBrowser or StatsRecorder.Metrics.DownloadDesktop)
            .GroupBy(c => int.TryParse(c.Key, out var id) ? id : 0)
            .Where(g => modsByGroup.ContainsKey(g.Key))
            .Select(g => (Group: g.Key, Downloads: g.Sum(c => c.Count)))
            .OrderByDescending(g => g.Downloads)
            .Take(TopCount)
            .Select(g => new TopMod
            {
                GroupId = g.Group,
                ModId = modsByGroup[g.Group].ModId,
                Name = modsByGroup[g.Group].Name,
                Author = modsByGroup[g.Group].Author,
                Downloads = g.Downloads
            })
            .ToList();

        List<NamedCount> Top(string metric, int count) => current
            .Where(c => c.Metric == metric && c.Key.Length > 0)
            .GroupBy(c => c.Key)
            .Select(g => new NamedCount { Name = g.Key, Count = g.Sum(c => c.Count) })
            .OrderByDescending(n => n.Count).ThenBy(n => n.Name)
            .Take(count)
            .ToList();
        result.Searches = Top(StatsRecorder.Metrics.Search, TopSearchCount);
        result.SearchesWithoutResults = Top(StatsRecorder.Metrics.SearchWithoutResults, TopSearchCount);
        result.TagFilters = Top(StatsRecorder.Metrics.TagFilter, TopSearchCount);
        result.DesktopVersions = Top(StatsRecorder.Metrics.DesktopSync, TopCount);
        result.TagsSuggested = Top(StatsRecorder.Metrics.TagSuggested, TopSearchCount);
        result.TagsRequested = Top(StatsRecorder.Metrics.TagRequested, TopSearchCount);
    }

    /// <summary>Warnings and errors per day, from the site log (which keeps 90 days).</summary>
    private async Task AddProblemsAsync(SiteStats result, CancellationToken ct)
    {
        var since = result.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var problems = await log.Entries.AsNoTracking()
            .Where(e => e.Timestamp >= since && e.Level >= LogSeverity.Warning)
            .Select(e => new { e.Timestamp, e.Level })
            .ToListAsync(ct);

        var byDay = problems.ToLookup(p => DateOnly.FromDateTime(p.Timestamp));
        foreach (var day in EachDay(result.From, result.To))
        {
            result.Problems.Add(new DailyProblems
            {
                Day = day,
                Warnings = byDay[day].Count(p => p.Level == LogSeverity.Warning),
                Errors = byDay[day].Count(p => p.Level >= LogSeverity.Error)
            });
        }
    }

    /// <summary>Account-link verifications started in the period, by outcome.</summary>
    private async Task AddVerificationsAsync(SiteStats result, CancellationToken ct)
    {
        var since = result.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var started = await db.F95PendingVerifications.AsNoTracking()
            .Where(v => v.CreatedAt >= since)
            .Select(v => new { v.Status, v.CreatedAt })
            .ToListAsync(ct);

        // A pending code nobody checked after it ran out is still marked pending; it expired all the same.
        var now = DateTime.UtcNow;
        bool Expired(F95VerificationStatus status, DateTime created) =>
            status == F95VerificationStatus.Expired || (status == F95VerificationStatus.Pending && now - created > VerificationLifetime);

        result.Verifications = new VerificationStats
        {
            Verified = started.Count(v => v.Status == F95VerificationStatus.Verified),
            Cancelled = started.Count(v => v.Status == F95VerificationStatus.Cancelled),
            Expired = started.Count(v => Expired(v.Status, v.CreatedAt)),
            Pending = started.Count(v => v.Status == F95VerificationStatus.Pending && !Expired(v.Status, v.CreatedAt))
        };
    }

    private static IEnumerable<DateOnly> EachDay(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
            yield return day;
    }
}
