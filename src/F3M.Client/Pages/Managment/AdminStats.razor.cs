using F3M.Client.Business;
using F3M.Client.Components.Charts;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;
using System.Globalization;

namespace F3M.Client.Pages.Managment;

public partial class AdminStats
{
    /// <summary>The periods to choose from, in days.</summary>
    private static readonly int[] Ranges = [7, 30, 90, 365];

    /// <summary>Longer periods are drawn per week: a column per day would be too thin to point at.</summary>
    private const int MaxDailyPoints = 90;

    private int days = 30;
    private SiteStats? stats;
    private bool loading = true;
    private string? error;
    private int generation;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task SelectRange(int range)
    {
        if (range == days && stats is not null)
            return;
        days = range;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var current = ++generation;
        loading = true;
        error = null;
        try
        {
            var result = await Api.GetStatsAsync(days);
            if (current != generation)
                return;
            stats = result;
        }
        catch (Exception)
        {
            if (current == generation)
                error = "The statistics could not be loaded. Please try again.";
        }
        finally
        {
            if (current == generation)
                loading = false;
        }
    }

    // ── Periods and changes ───────────────────────────────────────────────────

    private string PeriodName => days switch
    {
        7 => "last 7 days",
        30 => "last 30 days",
        90 => "last 90 days",
        365 => "last year",
        _ => $"last {days} days"
    };

    private string ChangeNote => $"vs the {days} days before";

    /// <summary>
    /// Counted numbers (downloads, visits, ...) only exist since counting began; before that the previous period is
    /// empty, not zero, so no change is shown.
    /// </summary>
    private bool PreviousCounted => stats?.CountingSince is { } since && since <= stats.From.AddDays(-stats.Days);

    /// <summary>The change as a fraction, or null when there is nothing to compare with.</summary>
    private static double? Change(double current, double previous, bool comparable = true) =>
        !comparable || previous <= 0 ? null : (current - previous) / previous;

    private bool CountingStartedInPeriod => stats?.CountingSince is { } since && since > stats.From;

    // ── Series ────────────────────────────────────────────────────────────────

    private bool Weekly => stats is not null && stats.Days > MaxDailyPoints;

    /// <summary>
    /// Days as chart points: one per day, or per week (Monday to Sunday) for long periods. Weekly points are sums, or
    /// averages per day for numbers that can't be added up (visitors are counted once per day).
    /// </summary>
    private ChartPoints ToPoints<T>(IReadOnlyList<T> source, Func<T, DateOnly> day, bool average, params Func<T, double>[] values)
    {
        if (!Weekly)
        {
            // Today isn't over yet; say so, so its lower numbers don't read as a drop.
            return new ChartPoints(
                source.Select(d => day(d) == stats!.To ? "Today, so far" : day(d).ToString("ddd, MMM d", CultureInfo.InvariantCulture)).ToList(),
                source.Select(d => day(d).ToString("MMM d", CultureInfo.InvariantCulture)).ToList(),
                source.Select(d => values.Select(v => v(d)).ToArray()).ToList());
        }

        var weeks = source.GroupBy(d => WeekStart(day(d))).ToList();
        return new ChartPoints(
            weeks.Select(w => $"Week of {w.Key.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}{(w.Any(d => day(d) == stats!.To) ? " (so far)" : "")}").ToList(),
            weeks.Select(w => w.Key.ToString("MMM d", CultureInfo.InvariantCulture)).ToList(),
            weeks.Select(w => values.Select(v => average ? Math.Round(w.Average(v), 1) : w.Sum(v)).ToArray()).ToList());
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static List<ChartSeries> Series(ChartPoints points, params (string Name, string Color)[] series) =>
        series.Select((s, i) => new ChartSeries(s.Name, s.Color, points.Values.Select(v => v[i]).ToList())).ToList();

    private ChartPoints DownloadPoints => ToPoints(stats!.Downloads, d => d.Day, false, d => d.Browser, d => d.Desktop);
    private ChartPoints VisitPoints => ToPoints(stats!.Visits, d => d.Day, Weekly, d => d.Visitors, d => d.PageLoads);
    private ChartPoints SignInPoints => ToPoints(stats!.SignIns, d => d.Day, false, d => d.Password, d => d.F95);
    private ChartPoints ProblemPoints => ToPoints(stats!.Problems, d => d.Day, false, d => d.Errors, d => d.Warnings);

    private ChartPoints GrowthPoints => new(
        stats!.Growth.Select(m => m.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)).ToList(),
        stats.Growth.Select(m => m.Month.ToString("MMM", CultureInfo.InvariantCulture)).ToList(),
        stats.Growth.Select(m => new double[] { m.NewMods, m.NewVersions, m.NewUsers }).ToList());

    private ChartPoints TagSuggestionPoints => new(
        stats!.TagSuggestions.Select(w => $"Week of {w.Week.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}").ToList(),
        stats.TagSuggestions.Select(w => w.Week.ToString("MMM d", CultureInfo.InvariantCulture)).ToList(),
        stats.TagSuggestions.Select(w => new double[] { w.Suggested, w.Requested, w.Decided }).ToList());

    private static readonly (string, string) SuggestedSeries = ("Suggested", ChartColors.Series1);
    private static readonly (string, string) RequestedSeries = ("Asked for", ChartColors.Series2);
    private static readonly (string, string) BrowserSeries = ("Browser", ChartColors.Series1);
    private static readonly (string, string) DesktopSeries = ("Desktop app", ChartColors.Series2);
    private static readonly (string, string) PasswordSeries = ("Password", ChartColors.Series1);
    private static readonly (string, string) F95Series = ("F95zone verification", ChartColors.Series2);
    private static readonly (string, string) ErrorSeries = ("Errors", ChartColors.Critical);
    private static readonly (string, string) WarningSeries = ("Warnings", ChartColors.Warning);
    private static readonly (string, string) NewModSeries = ("New mods", ChartColors.Series1);
    private static readonly (string, string) NewVersionSeries = ("New versions", ChartColors.Series2);

    // ── Lists ─────────────────────────────────────────────────────────────────

    private List<ChartItem> TopModItems(List<TopMod> mods) =>
        mods.Select(m => new ChartItem($"{m.Name} · {m.Author}", m.Downloads, Paths.ModDetail(m.ModId))).ToList();

    private List<ChartItem> CategoryModItems => stats!.Categories.Select(c => new ChartItem(c.Name, c.Mods)).ToList();

    private List<ChartItem> CategoryDownloadItems => stats!.Categories
        .Where(c => c.Downloads > 0)
        .OrderByDescending(c => c.Downloads)
        .Select(c => new ChartItem(c.Name, c.Downloads))
        .ToList();

    private List<ChartItem> CategoryStorageItems => stats!.Categories
        .Where(c => c.StorageBytes > 0)
        .OrderByDescending(c => c.StorageBytes)
        .Select(c => new ChartItem(c.Name, c.StorageBytes))
        .ToList();

    private List<ChartItem> DesktopVersionItems => stats!.DesktopVersions.Select(v => new ChartItem($"v{v.Name}", v.Count)).ToList();

    private List<ChartItem> VerificationItems =>
    [
        new("Verified", stats!.Verifications.Verified, Color: ChartColors.Good, Icon: "bi-check-circle-fill"),
        new("Still waiting for the code", stats.Verifications.Pending, Color: ChartColors.Neutral, Icon: "bi-hourglass-split"),
        new("Expired after 24 hours", stats.Verifications.Expired, Color: ChartColors.Warning, Icon: "bi-clock-history"),
        new("Cancelled or refused", stats.Verifications.Cancelled, Color: ChartColors.Serious, Icon: "bi-x-circle-fill"),
    ];

    private string? VerificationRate
    {
        get
        {
            var v = stats!.Verifications;
            var finished = v.Verified + v.Expired + v.Cancelled;
            return finished == 0 ? null : $"{(double)v.Verified / finished:P0} of finished verifications succeeded";
        }
    }

    private static string Bytes(double bytes) => ((long)bytes).FileSize();
}
