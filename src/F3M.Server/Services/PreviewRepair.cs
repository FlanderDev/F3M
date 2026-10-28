using F3M.Server.Data;
using F3M.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>
/// Once after startup: brings the stored previews in line with <see cref="PreviewImages"/>. Previews imported before
/// the server checked their bytes are often AVIF named .jpg or .png, which the desktop app can't show; they become
/// WebP. A preview whose extension names another format than its bytes gets the right one. Previews that are fine are
/// only identified (ImageMagick reads their header, not their pixels), so later starts cost little.
/// </summary>
public sealed class PreviewRepair(IServiceScopeFactory scopes, ILogger<PreviewRepair> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The site comes up first; nothing here is urgent.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        try
        {
            await RepairAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checking the stored preview images failed.");
        }
    }

    private async Task RepairAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();

        var names = await db.Mods.Where(m => m.PreviewImageName != null && m.PreviewImageName != "")
            .Select(m => m.PreviewImageName!).Distinct().ToListAsync(ct);

        var repaired = 0;
        var unreadable = new List<string>();
        var republish = new HashSet<int>();
        foreach (var name in names)
        {
            var path = Path.Combine(Assets.Images, name);
            if (!File.Exists(path))
                continue;

            // From the bytes, not the path: given a file name, ImageMagick would also go by its extension.
            var original = await File.ReadAllBytesAsync(path, ct);
            PreviewFormat format;
            byte[] bytes;
            string extension;
            try
            {
                format = PreviewImages.Identify(original);
                if (PreviewImages.Extension(format) is { } expected && expected == NormalExtension(name))
                    continue;
                (bytes, extension) = PreviewImages.Normalize(original);
            }
            catch (InvalidDataException ex)
            {
                unreadable.Add($"{name} ({ex.Message})");
                continue;
            }

            // A new name, so browsers and the desktop app don't keep the old file from their cache.
            var newName = $"{Guid.NewGuid():N}{extension}";
            await File.WriteAllBytesAsync(Path.Combine(Assets.Images, newName), bytes, ct);

            var versions = await db.Mods.Where(m => m.PreviewImageName == name).ToListAsync(ct);
            foreach (var version in versions)
            {
                version.PreviewImageName = newName;
                if (version.IsApproved)
                    republish.Add(version.Id);
            }
            await db.SaveChangesAsync(ct);

            try { File.Delete(path); }
            catch (IOException) { /* an orphaned image is harmless */ }

            repaired++;
            logger.LogInformation("The preview {OldName} ({Format}) is stored as {NewName} now.", name, format, newName);
        }

        // The catalog names the previews, so the versions that use a renamed one are published again.
        foreach (var versionId in republish)
            await catalog.TryPublishVersionAsync(versionId, ct);

        if (repaired > 0)
            logger.LogInformation("Repaired {Count} preview images; {Versions} catalog entries were updated.", repaired, republish.Count);
        if (unreadable.Count > 0)
            logger.LogWarning("These preview images are no image the site can show; replace them on their mods' edit pages: {Previews}", string.Join(", ", unreadable));
    }

    /// <summary>".jpeg" is stored as ".jpg" too; other extensions as they are, in lower case.</summary>
    private static string NormalExtension(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpeg" ? ".jpg" : Path.GetExtension(name).ToLowerInvariant();
}
