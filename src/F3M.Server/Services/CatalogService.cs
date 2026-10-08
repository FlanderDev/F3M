using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F3M.Server.Data;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace F3M.Server.Services;

/// <summary>
/// Writes the public, signed catalog (plan section 5): one document per mod version, plus index.json.
///
/// Every file is a signed envelope: { "keyId", "document", "signature" }. "document" is the JSON text of the payload,
/// stored as a string, and the signature covers exactly those UTF-8 bytes. Clients verify the signature before parsing.
/// </summary>
public sealed class CatalogService(
    AppDbContext db,
    IOptions<CatalogOptions> options,
    ILogger<CatalogService> logger,
    IHostEnvironment environment)
{
    public const int SchemaVersion = 1;

    // One writer at a time across requests in this process, so index.json is never rebuilt from a half-written state.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CatalogOptions _opts = options.Value;

    // ── Startup ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the folders, creates the signing key if it is missing, and publishes the public key. Configuration
    /// errors (no password outside Development, wrong password) throw, so the server refuses to start rather than
    /// serve an unsigned catalog.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.Combine(_opts.Directory, "mods"));

        await Gate.WaitAsync(ct);
        try
        {
            EnsureKey();
        }
        finally
        {
            Gate.Release();
        }

        // Takes the gate itself, so it runs after the lock above is released.
        await PublishPublicKeyAsync(ct);
    }

    /// <summary>Creates the encrypted signing key when no key file exists yet. Existing keys are never replaced.</summary>
    private void EnsureKey()
    {
        if (File.Exists(_opts.KeyPath))
            return;

        var password = ResolvePassword();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(
            password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

        Directory.CreateDirectory(Path.GetDirectoryName(_opts.KeyPath)!);

        // Owner-only from the moment the file exists (honoured on Linux, ignored on Windows).
        var temporary = _opts.KeyPath + ".tmp";
        using (var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(pem);
        }

        File.Move(temporary, _opts.KeyPath, overwrite: false);
        logger.LogWarning(
            "Created a new catalog signing key at {KeyPath}. Back it up together with its password; without it, existing signatures can no longer be reproduced.",
            _opts.KeyPath);
    }

    /// <summary>The configured password, or the Development default when running in Development.</summary>
    private string ResolvePassword()
    {
        if (!string.IsNullOrEmpty(_opts.KeyPassword))
            return _opts.KeyPassword;

        if (environment.IsDevelopment())
            return CatalogOptions.DevelopmentKeyPassword;

        throw new InvalidOperationException(
            "The catalog signing key password is not set. Set the Catalog__KeyPassword environment variable before starting the server.");
    }

    // ── Operations used by uploads, edits and deletes ───────────────────────

    /// <summary>Writes one version's document and refreshes the index. Does nothing if the version no longer exists.</summary>
    public async Task PublishVersionAsync(int versionId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var key = LoadKey();

            var mod = await db.Mods.AsNoTracking()
                .Include(m => m.Files)
                .Include(m => m.GeneratedPaths)
                .Include(m => m.DependencyGroups)
                .FirstOrDefaultAsync(m => m.Id == versionId, ct);

            if (mod is null || !mod.IsApproved)
                return;

            var group = await db.ModGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == mod.ModGroupId, ct);
            var entries = await LoadEntriesAsync(mod.Files.Select(f => f.Id), ct);

            await WriteSignedAsync(VersionPath(mod.ModGroupId, mod.Id), BuildVersionDocument(mod, group?.Author ?? mod.Author, entries), key, ct);
            await WriteIndexAsync(key, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes one version's document and refreshes the index.</summary>
    public async Task RemoveVersionAsync(int groupId, int versionId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var path = VersionPath(groupId, versionId);
            if (File.Exists(path))
                File.Delete(path);

            var groupDirectory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(groupDirectory) && !Directory.EnumerateFileSystemEntries(groupDirectory).Any())
                Directory.Delete(groupDirectory);

            using var key = LoadKey();
            await WriteIndexAsync(key, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Publish, logging instead of throwing. Used by requests that have already succeeded: a catalog failure must not
    /// fail the upload. The admin rebuild repairs anything missed here.
    /// </summary>
    public async Task TryPublishVersionAsync(int versionId, CancellationToken ct = default)
    {
        try
        {
            await PublishVersionAsync(versionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Publishing version {VersionId} to the catalog failed. Run the catalog rebuild to repair it.", versionId);
        }
    }

    /// <summary>Removal counterpart of <see cref="TryPublishVersionAsync"/>.</summary>
    public async Task TryRemoveVersionAsync(int groupId, int versionId, CancellationToken ct = default)
    {
        try
        {
            await RemoveVersionAsync(groupId, versionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Removing version {VersionId} from the catalog failed. Run the catalog rebuild to repair it.", versionId);
        }
    }

    // ── Admin rebuild ───────────────────────────────────────────────────────

    /// <summary>
    /// Brings the whole catalog up to date: fills in placements for files uploaded before catalog support, rewrites
    /// every version document, removes documents of versions that no longer exist, and rebuilds the index.
    /// Versions whose files cannot be read are left out and reported.
    /// </summary>
    public async Task<CatalogRebuildResult> RebuildAllAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var key = LoadKey();

            var errors = new List<string>();
            var failedMods = new HashSet<int>();
            var backfilled = await BackfillFilesAsync(failedMods, errors, ct);

            var mods = await db.Mods.AsNoTracking()
                .Include(m => m.Files)
                .Include(m => m.GeneratedPaths)
                .Include(m => m.DependencyGroups)
                .Where(m => m.IsApproved)
                .ToListAsync(ct);

            var authors = await db.ModGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Author, ct);
            var entries = await LoadEntriesAsync(null, ct);

            var published = new HashSet<(int GroupId, int VersionId)>();
            foreach (var mod in mods)
            {
                if (failedMods.Contains(mod.Id) || mod.Files.Any(f => f.Sha256.Length == 0))
                    continue;

                var author = authors.TryGetValue(mod.ModGroupId, out var groupAuthor) ? groupAuthor : mod.Author;
                await WriteSignedAsync(VersionPath(mod.ModGroupId, mod.Id), BuildVersionDocument(mod, author, entries), key, ct);
                published.Add((mod.ModGroupId, mod.Id));
            }

            var removed = DeleteStaleVersionFiles(published);
            await WriteIndexAsync(key, ct);

            logger.LogInformation(
                "Catalog rebuilt: {Published} versions published, {Removed} stale documents removed, {Backfilled} files backfilled, {Errors} errors.",
                published.Count, removed, backfilled, errors.Count);

            return new CatalogRebuildResult(backfilled, published.Count, removed, errors);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ── Document building ───────────────────────────────────────────────────

    private static VersionDocument BuildVersionDocument(
        Mod mod,
        string author,
        IReadOnlyDictionary<int, List<ModFileEntry>> entries)
    {
        var files = mod.Files
            .OrderBy(f => f.Id)
            .Select(f => new VersionFile(
                f.Id,
                f.OriginalName,
                f.IsArchive,
                f.TargetPath,
                f.Sha256,
                f.FileSizeBytes,
                EntriesOf(entries, f.Id)
                    .Select(e => new PlacementRef(e.From, e.To, e.Sha256, e.Size, EnumName(e.Kind)))
                    .ToList()))
            .ToList();

        var dependencies = mod.DependencyGroups
            .OrderBy(g => g.Id)
            .Select(g => new DependencyRef(g.Id, "latest"))
            .ToList();

        var generatedPaths = mod.GeneratedPaths
            .OrderBy(p => p.Id)
            .Select(p => new GeneratedPathRef(p.Pattern, EnumName(p.Kind)))
            .ToList();

        return new VersionDocument(
            SchemaVersion,
            mod.ModGroupId,
            mod.Id,
            mod.Version,
            mod.Name,
            author,
            mod.Description,
            mod.Category,
            Utc(mod.UploadedAt),
            PreviewUrl(mod.PreviewImageName),
            files,
            dependencies,
            generatedPaths);
    }

    private static IEnumerable<ModFileEntry> EntriesOf(IReadOnlyDictionary<int, List<ModFileEntry>> entries, int fileId) =>
        entries.TryGetValue(fileId, out var list) ? list : Enumerable.Empty<ModFileEntry>();

    private async Task<Dictionary<int, List<ModFileEntry>>> LoadEntriesAsync(IEnumerable<int>? fileIds, CancellationToken ct)
    {
        IQueryable<ModFileEntry> query = db.ModFileEntries.AsNoTracking();
        if (fileIds is not null)
        {
            var ids = fileIds.ToList();
            query = query.Where(e => ids.Contains(e.ModFileId));
        }

        var rows = await query.OrderBy(e => e.Id).ToListAsync(ct);
        return rows.GroupBy(e => e.ModFileId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private async Task WriteIndexAsync(ECDsa key, CancellationToken ct)
    {
        var rows = await (
                from m in db.Mods.AsNoTracking()
                join g in db.ModGroups.AsNoTracking() on m.ModGroupId equals g.Id
                where m.IsApproved && m.IsLatestVersion
                select new { GroupId = g.Id, m.Name, g.Author, m.Category, VersionId = m.Id, m.Version, m.UploadedAt, m.PreviewImageName })
            .ToListAsync(ct);

        var groups = rows
            .OrderBy(r => r.GroupId)
            .Select(r => new IndexGroup(
                r.GroupId,
                r.Name,
                r.Author,
                r.Category,
                r.VersionId,
                r.Version,
                Utc(r.UploadedAt),
                PreviewUrl(r.PreviewImageName)))
            .ToList();

        await WriteSignedAsync(Path.Combine(_opts.Directory, "index.json"), new IndexDocument(SchemaVersion, DateTime.UtcNow, groups), key, ct);
    }

    // ── Backfill and cleanup ────────────────────────────────────────────────

    /// <summary>Inspects files uploaded before catalog support. A file that cannot be read marks its version as failed.</summary>
    private async Task<int> BackfillFilesAsync(HashSet<int> failedMods, List<string> errors, CancellationToken ct)
    {
        var pending = await db.ModFiles
            .Where(f => f.Sha256 == string.Empty)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);

        var done = 0;
        foreach (var file in pending)
        {
            try
            {
                var inspection = ModFileInspector.Inspect(Path.Combine(Assets.Files, file.FileName), file.OriginalName, file.InstallPath);

                file.Sha256 = inspection.Sha256;
                file.IsArchive = inspection.IsArchive;
                file.TargetPath = inspection.TargetPath;
                db.ModFileEntries.AddRange(inspection.Entries.Select(e => new ModFileEntry
                {
                    ModFileId = file.Id,
                    From = e.From,
                    To = e.To,
                    Size = e.Size,
                    Sha256 = e.Sha256,
                    Kind = e.Kind,
                }));

                await db.SaveChangesAsync(ct);
                done++;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                failedMods.Add(file.ModId);
                errors.Add($"Version {file.ModId}, '{file.OriginalName}': {ex.Message}");
            }
        }

        return done;
    }

    /// <summary>Deletes version documents that are not in <paramref name="keep"/>, and empty group folders.</summary>
    private int DeleteStaleVersionFiles(ISet<(int GroupId, int VersionId)> keep)
    {
        var modsDirectory = Path.Combine(_opts.Directory, "mods");
        if (!Directory.Exists(modsDirectory))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(modsDirectory, "*.json", SearchOption.AllDirectories).ToList())
        {
            var groupDirectory = Path.GetDirectoryName(file)!;
            if (!int.TryParse(Path.GetFileName(groupDirectory), out var groupId)
                || !int.TryParse(Path.GetFileNameWithoutExtension(file), out var versionId))
            {
                continue;
            }

            if (keep.Contains((groupId, versionId)))
                continue;

            File.Delete(file);
            removed++;

            if (!Directory.EnumerateFileSystemEntries(groupDirectory).Any())
                Directory.Delete(groupDirectory);
        }

        return removed;
    }

    // ── Signing and writing ─────────────────────────────────────────────────

    private async Task PublishPublicKeyAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var key = LoadKey();
            var keysPath = Path.Combine(_opts.Directory, "keys.json");

            // Keep older keys listed, so documents signed before a rotation still verify.
            var keys = new List<PublicKeyEntry>();
            if (File.Exists(keysPath))
                keys = JsonSerializer.Deserialize<KeysDocument>(await File.ReadAllTextAsync(keysPath, ct), Json)?.Keys ?? new List<PublicKeyEntry>();

            keys.RemoveAll(k => k.KeyId == _opts.KeyId);
            keys.Add(new PublicKeyEntry(_opts.KeyId, "ECDSA-P256-SHA256", key.ExportSubjectPublicKeyInfoPem()));

            await WriteAtomicAsync(keysPath, JsonSerializer.Serialize(new KeysDocument(keys), Json), ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task WriteSignedAsync(string path, object payload, ECDsa key, CancellationToken ct)
    {
        var document = JsonSerializer.Serialize(payload, Json);
        var signature = key.SignData(Encoding.UTF8.GetBytes(document), HashAlgorithmName.SHA256);
        var envelope = new SignedEnvelope(_opts.KeyId, document, Convert.ToBase64String(signature));

        await WriteAtomicAsync(path, JsonSerializer.Serialize(envelope, Json), ct);
    }

    /// <summary>Writes to a temporary file and moves it into place, so readers never see a partial document.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, ct);
        File.Move(temporary, path, overwrite: true);
    }

    private ECDsa LoadKey()
    {
        if (!File.Exists(_opts.KeyPath))
            throw new InvalidOperationException($"No catalog signing key at '{_opts.KeyPath}'. Restart the server to create one.");

        var password = ResolvePassword();
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(File.ReadAllText(_opts.KeyPath), password);
            return key;
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new InvalidOperationException("The catalog signing key could not be decrypted. Check Catalog__KeyPassword.", ex);
        }
    }

    private string VersionPath(int groupId, int versionId) =>
        Path.Combine(_opts.Directory, "mods", groupId.ToString(), $"{versionId}.json");

    private static string? PreviewUrl(string? imageName) =>
        string.IsNullOrEmpty(imageName) ? null : imageName.ImageUrl();

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string EnumName<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();
}

public sealed record CatalogRebuildResult(
    int FilesBackfilled,
    int VersionsPublished,
    int VersionsRemoved,
    IReadOnlyList<string> Errors);

// ── Document shapes (camelCase on the wire, see JsonSerializerDefaults.Web) ──

public sealed record VersionDocument(
    int Schema,
    int GroupId,
    int VersionId,
    string Version,
    string Name,
    string Author,
    string Description,
    string Category,
    DateTime PublishedAt,
    string? PreviewUrl,
    IReadOnlyList<VersionFile> Files,
    IReadOnlyList<DependencyRef> Dependencies,
    IReadOnlyList<GeneratedPathRef> GeneratedPaths);

public sealed record VersionFile(
    int FileId,
    string OriginalName,
    bool IsArchive,
    string TargetPath,
    string Sha256,
    long Size,
    IReadOnlyList<PlacementRef> Placements);

public sealed record PlacementRef(string From, string To, string Sha256, long Size, string Kind);

/// <summary>"latest" follows the group's current approved version. Pins are local to a desktop profile, not published.</summary>
public sealed record DependencyRef(int GroupId, string Version);

public sealed record GeneratedPathRef(string Pattern, string Kind);

public sealed record IndexDocument(int Schema, DateTime GeneratedAt, IReadOnlyList<IndexGroup> Groups);

public sealed record IndexGroup(
    int GroupId,
    string Name,
    string Author,
    string Category,
    int LatestVersionId,
    string LatestVersion,
    DateTime UpdatedAt,
    string? PreviewUrl);

public sealed record KeysDocument(List<PublicKeyEntry> Keys);

public sealed record PublicKeyEntry(string KeyId, string Algorithm, string PublicKeyPem);

public sealed record SignedEnvelope(string KeyId, string Document, string Signature);
