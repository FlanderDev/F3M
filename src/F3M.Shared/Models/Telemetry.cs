using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public sealed class Telemetry
{
    /// <summary>
    /// An error the website ran into in a visitor's browser, as the browser sends it. The server stores it in the
    /// log (<see cref="LogEntry"/>). Its old table in the main database is no longer written to.
    /// </summary>
    public sealed class ErrorReport
    {
        [Key]
        public int Id { get; set; }
        public string? Message { get; set; }
        public string? StackTrace { get; set; }
        public string? Source { get; set; }
        public string? TargetSite { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static ErrorReport FromException(Exception exception) => new()
        {
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            Source = exception.Source,
            TargetSite = exception.TargetSite?.ToString(),
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>How important a log entry is. The numbers match Microsoft.Extensions.Logging.LogLevel.</summary>
public enum LogSeverity
{
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}

/// <summary>Where a log entry comes from.</summary>
public enum LogSource
{
    /// <summary>The server: what it did, and what went wrong there.</summary>
    Server = 0,

    /// <summary>A visitor's browser: errors the website ran into there.</summary>
    Browser = 1
}

/// <summary>Which sources the admin log shows.</summary>
public enum LogSourceFilter
{
    All = 0,
    Server = 1,
    Browser = 2
}

/// <summary>One entry of the site log (admin page). Kept in its own database, apart from the site's data.</summary>
public sealed class LogEntry
{
    [Key]
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogSeverity Level { get; set; }
    public LogSource Source { get; set; }

    /// <summary>The part of the server that wrote it (its logger's category), or the browser's error source.</summary>
    public string? Category { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>The exception with its stack trace, when there is one.</summary>
    public string? Details { get; set; }

    /// <summary>The signed-in user the request came from, if any.</summary>
    public string? UserName { get; set; }

    /// <summary>The request's method and path, for entries written while answering one.</summary>
    public string? RequestPath { get; set; }
}

/// <summary>One page of the log, newest first (admin page).</summary>
public sealed class LogPage
{
    public List<LogEntry> Items { get; set; } = [];

    /// <summary>How many entries match the filter in all.</summary>
    public int Total { get; set; }
}
