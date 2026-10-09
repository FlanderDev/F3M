using System.IO.Compression;
using System.Net;
using F3M.Desktop.Core;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.Services;

/// <summary>
/// Checks a version document before anything is downloaded or written (plan 5.5). Path rules come from
/// <see cref="InstallPaths"/> and <see cref="GeneratedPathRules"/> in F3M.Shared, the same code the server uses.
/// </summary>
public static class PlacementRules
{
    public static void ValidateVersion(VersionDocument doc)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in doc.Files)
        {
            if (!InstallPaths.TrySplit(file.OriginalName, out var nameParts, out _) || nameParts.Count != 1)
                throw new UserException($"'{file.OriginalName}' is not a valid file name in {doc.Name}.");

            if (file.IsArchive && !file.OriginalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new UserException($"{doc.Name} ships a {Path.GetExtension(file.OriginalName)} archive, which the desktop app cannot open yet.");

            var froms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var placement in file.Placements)
            {
                var from = Normalize(placement.From, "archive");
                if (!file.IsArchive && from != file.OriginalName)
                    throw new UserException($"{doc.Name} places {from}, which is not the file it ships.");
                if (!froms.Add(from))
                    throw new UserException($"{doc.Name} lists {from} more than once.");

                var to = Normalize(placement.To, "game folder");
                if (!targets.Add(to))
                    throw new UserException($"{doc.Name} writes {to} from two places.");
            }
        }

        if (doc.GeneratedPaths.Count > GeneratedPathRules.MaxPatternsPerVersion)
            throw new UserException($"{doc.Name} declares more than {GeneratedPathRules.MaxPatternsPerVersion} generated-file patterns.");

        foreach (var generated in doc.GeneratedPaths)
        {
            if (!GeneratedPathRules.TryNormalize(generated.Pattern, out var pattern, out var error))
                throw new UserException(error ?? $"{doc.Name} has an invalid generated-file pattern.");
            if (targets.Any(t => GeneratedPathRules.IsMatch(pattern, t)))
                throw new UserException($"A generated-file pattern of {doc.Name} matches one of its own files: {generated.Pattern}");
        }
    }

    /// <summary>A relative, normalised path. Throws a user message for anything else.</summary>
    public static string Normalize(string input, string label)
    {
        if (string.IsNullOrWhiteSpace(input) || input.StartsWith('/') || input.StartsWith('\\') || (input.Length >= 2 && input[1] == ':'))
            throw new UserException($"The {label} path '{input}' must be relative.");
        if (!InstallPaths.TryNormalize(input, out var normalized) || normalized.Length == 0)
            throw new UserException($"'{input}' is not a valid {label} path.");
        return normalized;
    }

    public static PlacementKind KindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "plugin" => PlacementKind.Plugin,
        "config" => PlacementKind.Config,
        _ => PlacementKind.Other,
    };

    public static GeneratedPathKind GeneratedKindOf(string kind) => kind.ToLowerInvariant() switch
    {
        "cache" => GeneratedPathKind.Cache,
        "userdata" => GeneratedPathKind.UserData,
        _ => GeneratedPathKind.Generated,
    };
}

/// <summary>
/// The verified local cache of mod versions (plan 6.3). A cached version is complete only when its version.json exists;
/// it is assembled in a staging folder and moved into place, so a crash never leaves a half-cached version.
/// </summary>
public sealed class Downloads(AppServices app)
{
    private const int MaxParallelFiles = 2;

    public string CacheDir(int groupId, int versionId) =>
        Path.Combine(app.Paths.Cache, groupId.ToString(), versionId.ToString());

    public bool IsCached(int groupId, int versionId) =>
        File.Exists(Path.Combine(CacheDir(groupId, versionId), "version.json"));

    /// <summary>Absolute path of one extracted file, or of the whole shipped file for plain uploads.</summary>
    public string PayloadPath(VersionDocument doc, int fileId, string from) =>
        FileOps.ResolveUnder(Path.Combine(CacheDir(doc.GroupId, doc.VersionId), "files", fileId.ToString()), from);

    /// <summary>Bytes still to download for the documents that are not cached yet.</summary>
    public long BytesToDownload(IEnumerable<VersionDocument> docs) =>
        docs.Where(d => !IsCached(d.GroupId, d.VersionId)).Sum(d => d.Files.Sum(f => f.Size));

    /// <summary>Downloads and verifies every version that is not cached yet. Runs inside an operation.</summary>
    public async Task EnsureCachedAsync(IEnumerable<VersionDocument> docs, OperationItem op, CancellationToken ct)
    {
        var missing = docs.Where(d => !IsCached(d.GroupId, d.VersionId)).ToList();
        foreach (var doc in missing) PlacementRules.ValidateVersion(doc);

        var index = 0;
        foreach (var doc in missing)
        {
            index++;
            op.SetDetail($"{doc.Name} {doc.Version} ({index} of {missing.Count})");
            await InstallAsync(doc, op, ct);
        }

        if (missing.Count > 0) EvictToLimit();
    }

    /// <summary>Marks a version as used now, so least-recently-used eviction keeps recent ones.</summary>
    public void Touch(int groupId, int versionId)
    {
        var marker = Path.Combine(CacheDir(groupId, versionId), "version.json");
        if (File.Exists(marker)) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
    }

    /// <summary>Removes the least recently used versions until the cache is under the limit. Protected versions stay.</summary>
    public void EvictToLimit()
    {
        var limit = (long)app.Settings.CacheLimitGb * 1024 * 1024 * 1024;
        var protectedVersions = ProtectedVersions();
        var entries = CachedVersions().ToList();
        var total = entries.Sum(e => e.Size);

        foreach (var entry in entries.Where(e => !protectedVersions.Contains((e.GroupId, e.VersionId))).OrderBy(e => e.LastUsed))
        {
            if (total <= limit) break;
            TryDeleteTree(entry.Path);
            total -= entry.Size;
            AppLog.Info($"Evicted cache {entry.GroupId}/{entry.VersionId} to stay under the limit");
        }
    }

    /// <summary>What "Clear unused cache" would free: versions not deployed and not pinned in any profile.</summary>
    public (int Count, long Bytes) UnusedCache()
    {
        var protectedVersions = ProtectedVersions();
        var unused = CachedVersions().Where(e => !protectedVersions.Contains((e.GroupId, e.VersionId))).ToList();
        return (unused.Count, unused.Sum(e => e.Size));
    }

    public void ClearUnused()
    {
        var protectedVersions = ProtectedVersions();
        foreach (var entry in CachedVersions().Where(e => !protectedVersions.Contains((e.GroupId, e.VersionId))))
            TryDeleteTree(entry.Path);
    }

    /// <summary>Staging folders left behind by a crash. Only called at startup, when no download is running.</summary>
    public void CleanStaging()
    {
        var staging = Path.Combine(app.Paths.Cache, ".staging");
        if (Directory.Exists(staging)) TryDeleteTree(staging);
    }

    private HashSet<(int, int)> ProtectedVersions()
    {
        var keep = new HashSet<(int, int)>();
        foreach (var mod in app.Deploy.LoadState().Mods) keep.Add((mod.GroupId, mod.VersionId));
        foreach (var profile in app.Profiles.List())
            foreach (var pin in profile.Pins) keep.Add((pin.Key, pin.Value));
        return keep;
    }

    private sealed record CacheEntry(string Path, int GroupId, int VersionId, long Size, DateTime LastUsed);

    private IEnumerable<CacheEntry> CachedVersions()
    {
        if (!Directory.Exists(app.Paths.Cache)) yield break;
        foreach (var groupDir in Directory.EnumerateDirectories(app.Paths.Cache))
        {
            if (!int.TryParse(Path.GetFileName(groupDir), out var groupId)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(groupDir))
            {
                var marker = Path.Combine(versionDir, "version.json");
                if (!int.TryParse(Path.GetFileName(versionDir), out var versionId) || !File.Exists(marker)) continue;
                yield return new CacheEntry(versionDir, groupId, versionId,
                    FileOps.DirectorySize(versionDir), File.GetLastWriteTimeUtc(marker));
            }
        }
    }

    private async Task InstallAsync(VersionDocument doc, OperationItem op, CancellationToken ct)
    {
        Directory.CreateDirectory(app.Paths.Downloads);
        var staging = Path.Combine(app.Paths.Cache, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var payloadRoot = Path.Combine(staging, "files");
            foreach (var file in doc.Files)
            {
                var part = Path.Combine(app.Paths.Downloads, $"{doc.VersionId}-{file.FileId}.part");
                await DownloadAsync(doc, file, part, op, ct);

                var destination = Path.Combine(payloadRoot, file.FileId.ToString());
                op.SetDetail($"Extracting {file.OriginalName}");
                await Task.Run(() => Extract(file, part, destination), ct);
                File.Delete(part);

                await VerifyPlacementsAsync(file, destination, ct);
            }

            FileOps.WriteJsonAtomic(Path.Combine(staging, "version.json"), doc);
            var target = CacheDir(doc.GroupId, doc.VersionId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) TryDeleteTree(staging);
        }
    }

    private async Task DownloadAsync(VersionDocument doc, VersionFile file, string partPath, OperationItem op, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            app.ServerUri($"api/mods/{doc.VersionId}/download/{file.FileId}"));
        using var response = await app.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UserException($"{file.OriginalName} is no longer available on the server.");
        if (!response.IsSuccessStatusCode)
            throw new UserException($"The server refused {file.OriginalName} (HTTP {(int)response.StatusCode}).");

        long done = 0;
        var buffer = new byte[81920];
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(partPath))
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                op.Report(done, file.Size);
            }
        }

        var actual = await FileOps.Sha256FileAsync(partPath, ct);
        if (actual != file.Sha256 || new FileInfo(partPath).Length != file.Size)
        {
            File.Delete(partPath);
            throw new UserException($"{file.OriginalName} failed its checksum. The file may have changed on the server; try again.");
        }
    }

    /// <summary>Plain uploads are kept as one file under their name. Archives are extracted entry by entry, path-checked.</summary>
    private static void Extract(VersionFile file, string partPath, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!file.IsArchive)
        {
            File.Copy(partPath, Path.Combine(destination, file.OriginalName), overwrite: true);
            return;
        }

        try
        {
            using var archive = ZipFile.OpenRead(partPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var target = FileOps.ResolveUnder(destination, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (InvalidDataException)
        {
            throw new UserException($"{file.OriginalName} is not a valid zip archive.");
        }
    }

    /// <summary>Every placement's source must exist and match its own hash, not only the archive hash.</summary>
    private static async Task VerifyPlacementsAsync(VersionFile file, string payloadDir, CancellationToken ct)
    {
        foreach (var placement in file.Placements)
        {
            var source = FileOps.ResolveUnder(payloadDir, placement.From);
            if (!File.Exists(source))
                throw new UserException($"{placement.From} is missing from {file.OriginalName}.");
            if (new FileInfo(source).Length != placement.Size || await FileOps.Sha256FileAsync(source, ct) != placement.Sha256)
                throw new UserException($"{placement.From} does not match the catalog. Try the download again.");
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not delete {path}", ex);
        }
    }
}
