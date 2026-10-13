using F3M.Server.Data;
using F3M.Shared.Helpers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace F3M.Server.Services;

/// <summary>
/// Behind /healthz (Docker's HEALTHCHECK and the server scripts): the database answers, the storage volume takes
/// writes, and the catalog signing key opens. The endpoint only says Healthy or Unhealthy; the reason goes to the log.
/// </summary>
public sealed class SiteHealthCheck(AppDbContext db, CatalogService catalog, ILogger<SiteHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        string? problem = null;
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
                problem = "The database can't be opened.";
            else if (!StorageWritable())
                problem = $"The storage folder {Path.GetFullPath(Assets.StorageRoot)} takes no writes (full or read-only?).";
            else if (catalog.CheckKey() is (false, var message))
                problem = message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = $"Unexpected error: {ex.Message}";
        }

        if (problem is null)
            return HealthCheckResult.Healthy();

        logger.LogWarning("Health check failed: {Problem}", problem);
        return HealthCheckResult.Unhealthy(problem);
    }

    private static bool StorageWritable()
    {
        var probe = Path.Combine(Assets.StorageRoot, $".health-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "ok");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
