using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Logging;

/// <summary>
/// The site log, in its own SQLite file next to the main database. It has no migrations: the table is created when
/// the file is new. Keeping it apart lets it grow, be trimmed or be deleted without touching the site's data.
/// </summary>
public sealed class LogDbContext(DbContextOptions<LogDbContext> options) : DbContext(options)
{
    public DbSet<LogEntry> Entries => Set<LogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<LogEntry>();
        entry.ToTable("LogEntries");
        entry.HasIndex(e => e.Timestamp);
        entry.HasIndex(e => new { e.Level, e.Source });
    }
}
