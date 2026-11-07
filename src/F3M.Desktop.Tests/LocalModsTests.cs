using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.Tests;

/// <summary>
/// Local mods end to end against a temporary data folder: analysis, the cache, IDs, deletion, and the places where a
/// local mod must not be treated like a catalog one. Nothing here reaches the network: the catalog is signed with a
/// test key and loaded from disk.
/// </summary>
public sealed class LocalModsTests : IDisposable
{
    private const int MissionsV2GroupId = 2;

    private readonly string _root = Directory.CreateTempSubdirectory("f3m-local-mods-").FullName;
    private readonly string _archives;
    private readonly AppServices _app;

    public LocalModsTests()
    {
        _archives = Directory.CreateDirectory(Path.Combine(_root, "archives")).FullName;
        _app = new AppServices(new AppPaths(Path.Combine(_root, "data")));

        // Anything that tries the server fails at once instead of reaching the real site.
        _app.Settings.ServerUrl = "http://127.0.0.1:1";
    }

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Analyze_SuggestsTheTypeAndWhereTheFilesGo()
    {
        var path = Zip("MyMission.zip", ("MyMission/main.code", "start"), ("MyMission/meta.json", "{}"));

        var draft = _app.LocalMods.Analyze(path);

        Assert.Equal("MyMission", draft.Name);
        Assert.Equal(ModTemplates.MissionsV2, draft.TemplateId);
        Assert.Contains("CustomMissions2/MyMission/main.code", draft.Targets()!);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("setup.exe")]
    public void Analyze_RefusesFilesThatAreNotModArchives(string name)
    {
        var path = Path.Combine(_archives, name);
        File.WriteAllText(path, "x");

        Assert.Throws<UserException>(() => _app.LocalMods.Analyze(path));
    }

    [Fact]
    public void Analyze_RefusesADamagedArchive()
    {
        var path = Path.Combine(_archives, "broken.zip");
        File.WriteAllText(path, "not a zip");

        Assert.Throws<UserException>(() => _app.LocalMods.Analyze(path));
    }

    [Fact]
    public async Task AddAsync_CachesThePlacedFilesUnderNegativeIds()
    {
        var first = await AddPluginAsync("First");
        var second = await AddPluginAsync("Second");

        Assert.Equal((-1, -1), (first.GroupId, first.VersionId));
        Assert.Equal(-2, second.GroupId);
        Assert.True(_app.Downloads.IsCached(first.GroupId, first.VersionId));

        var placement = Assert.Single(Assert.Single(first.Files).Placements);
        Assert.Equal("BepInEx/plugins/First/First.dll", placement.To);
        Assert.Equal("plugin", placement.Kind);
        var cached = _app.Downloads.PayloadPath(first, 1, placement.From);
        Assert.Equal("First dll", File.ReadAllText(cached));

        Assert.Equal("First", _app.LocalMods.Get(-1)?.Name);
        Assert.Equal(2, _app.LocalMods.List().Count);
    }

    [Fact]
    public async Task AddAsync_WithAnInvalidInstallPath_WritesNothing()
    {
        var draft = _app.LocalMods.Analyze(Zip("bad.zip", ("a.dll", "x"))) with { InstallPath = "../outside" };

        await Assert.ThrowsAsync<UserException>(() => _app.LocalMods.AddAsync(draft, op: null, CancellationToken.None));

        Assert.Empty(_app.LocalMods.List());
        Assert.Empty(_app.Downloads.CachedVersionIds());
    }

    [Fact]
    public async Task AddAsync_DependsOnTheFrameworkTheTypeNeeds_WhenTheCatalogHasIt()
    {
        LoadSignedCatalog(new IndexGroup(MissionsV2GroupId, "Custom Missions v2", "Crisp2002", "BepInEx-Plugin", 10, "2.4.0", DateTime.UtcNow, null));
        var draft = _app.LocalMods.Analyze(Zip("Mission.zip", ("Mission/main.code", "start")));

        var doc = await _app.LocalMods.AddAsync(draft, op: null, CancellationToken.None);

        var dependency = Assert.Single(doc.Dependencies);
        Assert.Equal(MissionsV2GroupId, dependency.GroupId);
        Assert.Equal("latest", dependency.Version);
    }

    [Fact]
    public async Task AddAsync_HasNoDependency_WhenTheCatalogLacksTheFramework()
    {
        LoadSignedCatalog();
        var draft = _app.LocalMods.Analyze(Zip("Mission.zip", ("Mission/main.code", "start")));

        Assert.Equal(("Custom Missions v2", (IndexGroup?)null), _app.LocalMods.RequiredMod(draft.TemplateId));
        var doc = await _app.LocalMods.AddAsync(draft, op: null, CancellationToken.None);

        Assert.Empty(doc.Dependencies);
    }

    [Fact]
    public async Task ResolveAsync_UsesTheLocalDocument()
    {
        LoadSignedCatalog();
        var added = await AddPluginAsync("Local");

        var resolved = await _app.Catalog.ResolveAsync([added.GroupId], new Dictionary<int, int>(), CancellationToken.None);

        Assert.Equal("Local", Assert.Single(resolved).Value.Name);
        Assert.Equal("Local", _app.Catalog.NameOf(added.GroupId));
    }

    [Fact]
    public async Task DeleteUnused_DeletesWhatNoProfileUses_AndNeverReusesItsId()
    {
        var kept = await AddPluginAsync("Kept");
        var removed = await AddPluginAsync("Removed");
        var profile = _app.Profiles.Create("Mixed", [kept.GroupId, removed.GroupId, 5]);

        // What Remove in the Library does: out of the profile, then the cleanup.
        profile.GroupIds.Remove(removed.GroupId);
        _app.Profiles.Save(profile);
        var deleted = _app.LocalMods.DeleteUnused();

        Assert.Equal(["Removed"], deleted);
        Assert.Null(_app.LocalMods.Get(removed.GroupId));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_app.Downloads.CacheDir(removed.GroupId, removed.VersionId))));
        Assert.NotNull(_app.LocalMods.Get(kept.GroupId));
        Assert.True(_app.Downloads.IsCached(kept.GroupId, kept.VersionId));

        var next = await AddPluginAsync("Next");
        Assert.Equal(-3, next.GroupId);
    }

    [Fact]
    public async Task DeleteUnused_KeepsAModAProfileOfAnotherGameFolderUses()
    {
        var added = await AddPluginAsync("Shared");
        var otherGame = new ProfileDef { Name = "Other game", GroupIds = [added.GroupId] };
        FileOps.WriteJsonAtomic(Path.Combine(_app.Paths.Profiles, "other-game", otherGame.Id + ".json"), otherGame);

        Assert.Empty(_app.LocalMods.DeleteUnused());
        Assert.NotNull(_app.LocalMods.Get(added.GroupId));
    }

    [Fact]
    public async Task DeleteUnused_DeletesADeployedModToo_DeployRemovesItFromTheRecord()
    {
        var added = await AddPluginAsync("Deployed");
        var state = new DeployedState { Mods = [new DeployedMod { GroupId = added.GroupId, VersionId = added.VersionId }] };
        FileOps.WriteJsonAtomic(Path.Combine(_app.Paths.State, "some-game.json"), state);

        Assert.Equal(["Deployed"], _app.LocalMods.DeleteUnused());
    }

    [Fact]
    public async Task ClearUnusedCache_NeverCountsLocalMods()
    {
        await AddPluginAsync("Unused");

        Assert.Equal(0, _app.Downloads.UnusedCache().Count);
    }

    [Fact]
    public void WebLink_LeavesLocalModsOut()
    {
        var profile = _app.Profiles.Create("Shared", [-1, 7]);

        var link = _app.Profiles.WebLink(profile, out var leftOut);

        Assert.Equal(1, leftOut);
        Assert.Contains("mods=7", link);
        Assert.DoesNotContain("-1", link);
    }

    [Fact]
    public void WebLink_RefusesAProfileWithOnlyLocalMods()
    {
        var profile = _app.Profiles.Create("Only local", [-1, -2]);

        Assert.Throws<UserException>(() => _app.Profiles.WebLink(profile, out _));
    }

    private async Task<VersionDocument> AddPluginAsync(string name)
    {
        var draft = _app.LocalMods.Analyze(Zip($"{name}.zip", ($"BepInEx/plugins/{name}/{name}.dll", $"{name} dll")));
        return await _app.LocalMods.AddAsync(draft, op: null, CancellationToken.None);
    }

    private string Zip(string name, params (string Entry, string Content)[] files)
    {
        var path = Path.Combine(_archives, name);
        File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    /// <summary>Writes keys.json and an index signed with a fresh key, then loads them like the app does offline.</summary>
    private void LoadSignedCatalog(params IndexGroup[] groups)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var keys = new KeysDocument([new PublicKeyEntry("test", "ECDSA-P256-SHA256", key.ExportSubjectPublicKeyInfoPem())]);
        var document = JsonSerializer.Serialize(new IndexDocument(1, DateTime.UtcNow, groups), web);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(document), HashAlgorithmName.SHA256));

        FileOps.WriteJsonAtomic(_app.Paths.CatalogFile("keys.json"), keys);
        FileOps.WriteJsonAtomic(_app.Paths.CatalogFile("index.json"), new SignedEnvelope("test", document, signature));
        _app.Catalog.LoadCached();
        Assert.NotNull(_app.Catalog.Index);
    }
}
