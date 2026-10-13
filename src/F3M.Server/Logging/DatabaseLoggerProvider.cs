using F3M.Shared.Models;

namespace F3M.Server.Logging;

/// <summary>
/// Puts the server's log messages in the site log (admin page), next to the usual console output. Kept: everything the
/// site's own code logs from Information up, the host's start and stop, and errors from anywhere (the framework logs
/// unhandled request exceptions as errors). The framework's chatter below that stays on the console only.
/// </summary>
public sealed class DatabaseLoggerProvider(LogStore store, IHttpContextAccessor httpContextAccessor) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DatabaseLogger(categoryName, MinimumLevel(categoryName), store, httpContextAccessor);

    public void Dispose() { }

    private static LogLevel MinimumLevel(string category) =>
        category.StartsWith("F3M", StringComparison.Ordinal) || category == "Microsoft.Hosting.Lifetime"
            ? LogLevel.Information
            : LogLevel.Error;

    private sealed class DatabaseLogger(string category, LogLevel minimum, LogStore store, IHttpContextAccessor httpContextAccessor) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var context = httpContextAccessor.HttpContext;
            var user = context?.User.Identity is { IsAuthenticated: true } identity ? identity.Name : null;

            store.Write(new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = (LogSeverity)Math.Min((int)logLevel, (int)LogSeverity.Critical),
                Source = LogSource.Server,
                Category = category,
                Message = formatter(state, exception),
                Details = exception?.ToString(),
                UserName = user,
                RequestPath = context is null ? null : $"{context.Request.Method} {context.Request.Path}"
            });
        }
    }
}
