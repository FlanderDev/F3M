using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Statistics;

/// <summary>
/// Counted events for the statistics page (downloads, visits, searches, sign-ins, desktop syncs), in their own SQLite
/// file next to the main database, like the site log. It has no migrations: the tables are created when the file is
/// new. Only daily sums are kept, never single events, IP addresses or what a person did.
/// </summary>
public sealed class StatsDbContext(DbContextOptions<StatsDbContext> options) : DbContext(options)
{
    public DbSet<DailyCount> Counts => Set<DailyCount>();
    public DbSet<DailyVisitor> Visitors => Set<DailyVisitor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var count = modelBuilder.Entity<DailyCount>();
        count.ToTable("DailyCounts");
        count.HasKey(c => new { c.Day, c.Metric, c.Key });
        count.HasIndex(c => new { c.Metric, c.Day });

        var visitor = modelBuilder.Entity<DailyVisitor>();
        visitor.ToTable("DailyVisitors");
        visitor.HasKey(v => new { v.Day, v.Visitor });
    }
}

/// <summary>How often <see cref="Metric"/> happened on <see cref="Day"/>, per <see cref="Key"/> (a mod group, a search term, ...).</summary>
public sealed class DailyCount
{
    /// <summary>The UTC day, as yyyy-MM-dd, so it sorts and compares as text.</summary>
    public string Day { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public long Count { get; set; }
}

/// <summary>A browser that opened the site on a day. <see cref="Visitor"/> is a hash of the browser's random id.</summary>
public sealed class DailyVisitor
{
    public string Day { get; set; } = string.Empty;
    public string Visitor { get; set; } = string.Empty;
}
