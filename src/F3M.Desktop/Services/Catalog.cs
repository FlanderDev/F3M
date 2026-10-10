using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F3M.Desktop.Core;
using F3M.Shared.Models;

namespace F3M.Desktop.Services;

/// <summary>
/// Reads the static signed catalog under /catalog (plan 5). Every document is checked against the public keys before
/// it is used or cached. Keys are trusted on first use: a key id never changes its public key once seen.
/// </summary>
public sealed class Catalog(AppServices app)
{
    private const string Algorithm = "ECDSA-P256-SHA256";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keyPems = new(StringComparer.Ordinal);

    public IndexDocument? Index { get; private set; }

    /// <summary>True when the index was loaded from the local cache because the server could not be reached.</summary>
    public bool IndexFromCache { get; private set; }

    /// <summary>Loads the last verified index and keys, so Browse works offline after one good refresh.</summary>
    public void LoadCached()
    {
        try
        {
            var keys = FileOps.ReadJson<KeysDocument>(app.Paths.CatalogFile("keys.json"));
            if (keys is not null) MergeKeys(keys);

            var envelope = FileOps.ReadJson<SignedEnvelope>(app.Paths.CatalogFile("index.json"));
            if (envelope is not null && _keys.Count > 0)
            {
                Index = Verify<IndexDocument>(envelope);
                IndexFromCache = true;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UserException or FormatException)
        {
            AppLog.Error("Cached catalog could not be used", ex);
        }
    }

    /// <summary>Fetches keys and index. Falls back to the cached index when the server cannot be reached.</summary>
    public async Task RefreshIndexAsync(CancellationToken ct)
    {
        try
        {
            await RefreshKeysAsync(ct);
            var envelope = await FetchJsonAsync<SignedEnvelope>("catalog/index.json", ct)
                           ?? throw new UserException("The server has not published a catalog yet.");
            var document = Verify<IndexDocument>(envelope);
            FileOps.WriteJsonAtomic(app.Paths.CatalogFile("index.json"), envelope);
            Index = document;
            IndexFromCache = false;
        }
        catch (Exception ex) when (Index is not null && ex is not UserException && !ct.IsCancellationRequested)
        {
            AppLog.Info($"Catalog unavailable, using the cached index: {ex.Message}");
            IndexFromCache = true;
        }
    }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (Index is null) await RefreshIndexAsync(ct);
        if (Index is null) throw new UserException("The catalog is not available. Check the server address in Settings.");
    }

    public IndexGroup? Group(int groupId) => Index?.Groups.FirstOrDefault(g => g.GroupId == groupId);

    /// <summary>The version document for one version. Cached after the first verified download; versions never change.</summary>
    public async Task<VersionDocument> GetVersionAsync(int groupId, int versionId, CancellationToken ct)
    {
        var path = app.Paths.CatalogFile($"mods/{groupId}/{versionId}.json");
        if (_keys.Count == 0) await RefreshKeysAsync(ct);
        if (File.Exists(path))
        {
            try
            {
                return Verify<VersionDocument>(FileOps.ReadJson<SignedEnvelope>(path)!);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UserException or FormatException)
            {
                AppLog.Error($"Dropping unreadable cached document {groupId}/{versionId}", ex);
                File.Delete(path);
            }
        }

        var envelope = await FetchJsonAsync<SignedEnvelope>($"catalog/mods/{groupId}/{versionId}.json", ct)
                       ?? throw new UserException($"Version {versionId} of mod {groupId} is not in the catalog.");
        var document = Verify<VersionDocument>(envelope);
        if (document.GroupId != groupId || document.VersionId != versionId)
            throw new UserException("A catalog document does not match the version it was requested for.");

        FileOps.WriteJsonAtomic(path, envelope);
        return document;
    }

    /// <summary>
    /// The version document each group resolves to: its pin if the profile has one, otherwise the latest approved
    /// version, plus every dependency, transitively. The first resolution of a group wins.
    /// </summary>
    public async Task<Dictionary<int, VersionDocument>> ResolveAsync(
        IEnumerable<int> groupIds, IReadOnlyDictionary<int, int> pins, CancellationToken ct)
    {
        await EnsureIndexAsync(ct);
        var result = new Dictionary<int, VersionDocument>();
        var queue = new Queue<int>(groupIds.Distinct());

        while (queue.Count > 0)
        {
            var groupId = queue.Dequeue();
            if (result.ContainsKey(groupId)) continue;

            var group = Group(groupId);
            var versionId = pins.TryGetValue(groupId, out var pinned)
                ? pinned
                : group?.LatestVersionId ?? throw new UserException($"Mod {groupId} has no approved version yet.");

            var document = await GetVersionAsync(groupId, versionId, ct);
            result[groupId] = document;

            foreach (var dependency in document.Dependencies)
            {
                if (result.ContainsKey(dependency.GroupId)) continue;
                if (Group(dependency.GroupId) is null)
                    throw new UserException($"{document.Name} needs {dependency.GroupId}, which has no approved version yet.");
                queue.Enqueue(dependency.GroupId);
            }
        }

        return result;
    }

    /// <summary>Preview image bytes, fetched from the server's public image route.</summary>
    public async Task<byte[]?> FetchBytesAsync(string relative, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await app.Http.GetAsync(app.ServerUri(relative), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    private async Task RefreshKeysAsync(CancellationToken ct)
    {
        var keys = await FetchJsonAsync<KeysDocument>("catalog/keys.json", ct)
                   ?? throw new UserException("The server has not published its catalog keys.");
        MergeKeys(keys);
        FileOps.WriteJsonAtomic(app.Paths.CatalogFile("keys.json"),
            new KeysDocument(_keyPems.Select(k => new PublicKeyEntry(k.Key, Algorithm, k.Value)).ToList()));
    }

    private void MergeKeys(KeysDocument document)
    {
        foreach (var entry in document.Keys)
        {
            if (entry.Algorithm != Algorithm) continue;
            if (_keyPems.TryGetValue(entry.KeyId, out var known))
            {
                if (known != entry.PublicKeyPem)
                    throw new UserException($"Catalog key {entry.KeyId} has changed. The update was refused.");
                continue;
            }

            var key = ECDsa.Create();
            key.ImportFromPem(entry.PublicKeyPem);
            _keys[entry.KeyId] = key;
            _keyPems[entry.KeyId] = entry.PublicKeyPem;
        }
    }

    /// <summary>Checks the signature over the UTF-8 bytes of the document string, then parses it.</summary>
    private T Verify<T>(SignedEnvelope envelope)
    {
        if (!_keys.TryGetValue(envelope.KeyId, out var key))
            throw new UserException($"The catalog is signed with an unknown key ({envelope.KeyId}).");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw new UserException("A catalog document has a malformed signature and was not used.");
        }

        if (!key.VerifyData(Encoding.UTF8.GetBytes(envelope.Document), signature, HashAlgorithmName.SHA256))
            throw new UserException("A catalog document failed its signature check and was not used.");

        return JsonSerializer.Deserialize<T>(envelope.Document, Json)
               ?? throw new UserException("A catalog document is empty.");
    }

    private async Task<T?> FetchJsonAsync<T>(string relative, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await app.Http.GetAsync(app.ServerUri(relative), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token);
    }
}
