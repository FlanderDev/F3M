using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public sealed class Telemetry
{
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

/// <summary>One page of the stored error reports, newest first (admin page).</summary>
public sealed class ErrorReportPage
{
    public List<Telemetry.ErrorReport> Items { get; set; } = [];

    /// <summary>How many reports are stored in all.</summary>
    public int Total { get; set; }
}
