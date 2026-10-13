using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace F3M.Server.Logging;

/// <summary>
/// Collects log entries and writes them to the log database in the background, so logging never waits for the disk.
/// Old entries are trimmed: after <see cref="RetentionDays"/> days, and beyond <see cref="MaxEntries"/> entries.
/// </summary>
public sealed class LogStore : BackgroundService
{
    public const int RetentionDays = IAdminApi.LogRetentionDays;
    public const int MaxEntries = 200_000;

    // Bounds per field, so a runaway message (or a browser posting junk) can't bloat the file.
    private const int MaxMessage = 4_000;
    private const int MaxDetails = 32_000;
    private const int MaxShort = 500;

    private const int MaxBatch = 200;
    private static readonly TimeSpan TrimInterval = TimeSpan.FromHours(6);

    // When the writer falls behind (or the disk is gone), new entries are dropped rather than piling up in memory.
    private readonly Channel<LogEntry> queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly DbContextOptions<LogDbContext> options;

    public LogStore(string connectionString)
    {
        // Its own options without a logger factory: EF must not log about writing the log, or a failing write
        // would log an error, which would be written, which would fail again.
        options = new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlite(connectionString)
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Options;
    }

    /// <summary>Creates the log database if it is new. Called once at startup, before anything is written.</summary>
    public void EnsureCreated()
    {
        using var db = new LogDbContext(options);
        db.Database.EnsureCreated();
        // Readers (the admin page) and the writer don't block each other.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>Queues an entry. Never throws and never waits.</summary>
    public void Write(LogEntry entry)
    {
        entry.Message = Truncate(entry.Message, MaxMessage) ?? string.Empty;
        entry.Details = Truncate(entry.Details, MaxDetails);
        entry.Category = Truncate(entry.Category, MaxShort);
        entry.UserName = Truncate(entry.UserName, MaxShort);
        entry.RequestPath = Truncate(entry.RequestPath, MaxShort);
        queue.Writer.TryWrite(entry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextTrim = DateTime.UtcNow;
        var batch = new List<LogEntry>(MaxBatch);

        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < MaxBatch && queue.Reader.TryRead(out var entry))
                    batch.Add(entry);

                await SaveAsync(batch);
                batch.Clear();

                if (DateTime.UtcNow >= nextTrim)
                {
                    await TrimAsync();
                    nextTrim = DateTime.UtcNow + TrimInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: what is still queued is written below.
        }

        while (queue.Reader.TryRead(out var entry))
            batch.Add(entry);
        await SaveAsync(batch);
    }

    private async Task SaveAsync(List<LogEntry> batch)
    {
        if (batch.Count == 0)
            return;

        try
        {
            await using var db = new LogDbContext(options);
            db.Entries.AddRange(batch);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Not through ILogger: that would only queue another entry for the same broken database.
            Console.Error.WriteLine($"Writing {batch.Count} log entries failed: {ex.Message}");
        }
    }

    private async Task TrimAsync()
    {
        try
        {
            await using var db = new LogDbContext(options);
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            await db.Entries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync();

            // Then the count: everything older than the newest MaxEntries goes.
            var oldestKept = await db.Entries.OrderByDescending(e => e.Id).Skip(MaxEntries - 1).Select(e => (long?)e.Id).FirstOrDefaultAsync();
            if (oldestKept is { } id)
                await db.Entries.Where(e => e.Id < id).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Trimming the log failed: {ex.Message}");
        }
    }

    private static string? Truncate(string? text, int max) => text is { Length: > 0 } && text.Length > max ? text[..max] : text;
}
