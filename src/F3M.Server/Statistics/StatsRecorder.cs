using F3M.Shared.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace F3M.Server.Statistics;

/// <summary>
/// Counts events for the statistics page. Counting only adds to a sum in memory; the sums are written to the
/// statistics database every few seconds in the background, so a download or a page load never waits for the disk.
/// Old days are removed after <see cref="IStatsApi.RetentionDays"/> days.
/// </summary>
public sealed class StatsRecorder : BackgroundService
{
    /// <summary>What is counted. The key of each is noted beside it.</summary>
    public static class Metrics
    {
        public const string DownloadBrowser = "download.browser";   // mod group id
        public const string DownloadDesktop = "download.desktop";   // mod group id
        public const string PageLoad = "visit";                      // ""
        public const string Search = "search";                       // search term
        public const string SearchWithoutResults = "search.empty";   // search term
        public const string SignInPassword = "signin.password";      // ""
        public const string SignInF95 = "signin.f95";                // ""
        public const string DesktopSync = "desktop.sync";            // desktop app version
    }

    /// <summary>Search terms are counted up to this length; anything longer is cut.</summary>
    public const int MaxKeyLength = 60;

    // Anyone can send searches and visits, so the number of different keys (search terms, versions, visitors) a day
    // is capped: a script sending random words can't grow the file without end. Real use stays far below.
    private const int MaxKeysPerMetricPerDay = 5_000;
    private const int MaxVisitorsPerDay = 100_000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<(string Day, string Metric, string Key), long> pending = new();
    private readonly ConcurrentDictionary<(string Day, string Visitor), byte> pendingVisitors = new();

    // Keys seen today, for the caps. Cleared when the day changes; a restart starts the count again, which is fine.
    private readonly ConcurrentDictionary<(string Metric, string Key), byte> keysToday = new();
    private readonly ConcurrentDictionary<string, int> keyCountsToday = new();
    private readonly ConcurrentDictionary<string, byte> visitorsToday = new();
    private int visitorCountToday;
    private string today = Today();
    private readonly Lock dayLock = new();

    private readonly DbContextOptions<StatsDbContext> options;

    public StatsRecorder(string connectionString)
    {
        // No logger factory, like the log store: writing statistics should never fill the site log.
        options = new DbContextOptionsBuilder<StatsDbContext>()
            .UseSqlite(connectionString)
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Options;
    }

    /// <summary>Creates the statistics database if it is new. Called once at startup.</summary>
    public void EnsureCreated()
    {
        using var db = new StatsDbContext(options);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>Adds one to <paramref name="metric"/> for today. Never throws and never waits.</summary>
    public void Count(string metric, string key = "")
    {
        key = NormalizeKey(key);
        var day = CurrentDay();
        if (key.Length > 0 && !keysToday.ContainsKey((metric, key)))
        {
            if (keyCountsToday.GetValueOrDefault(metric) >= MaxKeysPerMetricPerDay)
                return;
            if (keysToday.TryAdd((metric, key), 0))
                keyCountsToday.AddOrUpdate(metric, 1, (_, count) => count + 1);
        }

        pending.AddOrUpdate((day, metric, key), 1, (_, count) => count + 1);
    }

    /// <summary>Notes that the browser with this random id visited today. Only a hash of the id is kept.</summary>
    public void Visitor(string visitorId)
    {
        if (string.IsNullOrWhiteSpace(visitorId) || visitorId.Length > 100)
            return;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visitorId)))[..32];
        var day = CurrentDay();
        if (visitorsToday.ContainsKey(hash) || Volatile.Read(ref visitorCountToday) >= MaxVisitorsPerDay)
            return;

        if (visitorsToday.TryAdd(hash, 0))
        {
            Interlocked.Increment(ref visitorCountToday);
            pendingVisitors.TryAdd((day, hash), 0);
        }
    }

    /// <summary>Search terms are counted case- and space-insensitively: "Texture  Pack" and "texture pack" are one.</summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var collapsed = string.Join(' ', key.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return collapsed.Length > MaxKeyLength ? collapsed[..MaxKeyLength] : collapsed;
    }

    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Today() => DayKey(DateOnly.FromDateTime(DateTime.UtcNow));

    private string CurrentDay()
    {
        var now = Today();
        if (now == today)
            return now;

        lock (dayLock)
        {
            if (now != today)
            {
                keysToday.Clear();
                keyCountsToday.Clear();
                visitorsToday.Clear();
                Volatile.Write(ref visitorCountToday, 0);
                today = now;
            }
        }
        return now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextTrim = DateTime.UtcNow;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(FlushInterval, stoppingToken);
                await FlushAsync();

                if (DateTime.UtcNow >= nextTrim)
                {
                    await TrimAsync();
                    nextTrim = DateTime.UtcNow + TrimInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: what is still counted is written below.
        }

        await FlushAsync();
    }

    private async Task FlushAsync()
    {
        var counts = new List<(string Day, string Metric, string Key, long Count)>();
        foreach (var key in pending.Keys)
            if (pending.TryRemove(key, out var count))
                counts.Add((key.Day, key.Metric, key.Key, count));

        var visitors = new List<(string Day, string Visitor)>();
        foreach (var key in pendingVisitors.Keys)
            if (pendingVisitors.TryRemove(key, out _))
                visitors.Add(key);

        if (counts.Count == 0 && visitors.Count == 0)
            return;

        try
        {
            await using var db = new StatsDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var (day, metric, key, count) in counts)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO DailyCounts (Day, Metric, Key, Count) VALUES ({0}, {1}, {2}, {3}) " +
                    "ON CONFLICT (Day, Metric, Key) DO UPDATE SET Count = Count + excluded.Count;",
                    day, metric, key, count);
            }
            foreach (var (day, visitor) in visitors)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT OR IGNORE INTO DailyVisitors (Day, Visitor) VALUES ({0}, {1});", day, visitor);
            }
            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            // Lost counts are only statistics; not worth retrying or growing memory for.
            Console.Error.WriteLine($"Writing statistics failed: {ex.Message}");
        }
    }

    private async Task TrimAsync()
    {
        try
        {
            await using var db = new StatsDbContext(options);
            var cutoff = DayKey(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-IStatsApi.RetentionDays));
            await db.Counts.Where(c => string.Compare(c.Day, cutoff) < 0).ExecuteDeleteAsync();
            await db.Visitors.Where(v => string.Compare(v.Day, cutoff) < 0).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Trimming the statistics failed: {ex.Message}");
        }
    }
}
