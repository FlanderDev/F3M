using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Shared.Tests;

public sealed class ModFileInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("f3m-inspector-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Inspect_PlainDll_GoesToPluginsAsAPlugin()
    {
        var path = Write("MyPlugin.dll", "plugin bytes");

        var result = ModFileInspector.Inspect(path, "MyPlugin.dll", installPath: null);

        Assert.False(result.IsArchive);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("MyPlugin.dll", entry.From);
        Assert.Equal("BepInEx/plugins/MyPlugin.dll", entry.To);
        Assert.Equal(PlacementKind.Plugin, entry.Kind);
        Assert.Equal(Sha256("plugin bytes"), entry.Sha256);
        Assert.Equal(result.Sha256, entry.Sha256);
    }

    [Fact]
    public void Inspect_Archive_PlacesEveryFileWithItsOwnHashAndKind()
    {
        var path = Zip("mod.zip",
            ("BepInEx/plugins/Mod/Mod.dll", "dll"),
            ("BepInEx/config/mod.cfg", "cfg"),
            ("BepInEx/plugins/Mod/readme.txt", "read me"));

        var result = ModFileInspector.Inspect(path, "mod.zip", installPath: null);

        Assert.True(result.IsArchive);
        Assert.Equal(string.Empty, result.TargetPath);
        var byTarget = result.Entries.ToDictionary(e => e.To);
        Assert.Equal(PlacementKind.Plugin, byTarget["BepInEx/plugins/Mod/Mod.dll"].Kind);
        Assert.Equal(PlacementKind.Config, byTarget["BepInEx/config/mod.cfg"].Kind);
        Assert.Equal(PlacementKind.Other, byTarget["BepInEx/plugins/Mod/readme.txt"].Kind);
        Assert.Equal(Sha256("cfg"), byTarget["BepInEx/config/mod.cfg"].Sha256);
        Assert.Equal(3, byTarget["BepInEx/config/mod.cfg"].Size);
    }

    [Fact]
    public void Inspect_ArchiveRoot_InstallsOnlyThatFolderWithoutIt()
    {
        var path = Zip("wrapped.zip",
            ("Wrapper/BepInEx/plugins/A.dll", "a"),
            ("Other/ignored.txt", "x"));

        var result = ModFileInspector.Inspect(path, "wrapped.zip", installPath: null, archiveRoot: "Wrapper");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Wrapper/BepInEx/plugins/A.dll", entry.From);
        Assert.Equal("BepInEx/plugins/A.dll", entry.To);
    }

    [Fact]
    public void Inspect_InstallPath_PutsTheArchiveContentBelowIt()
    {
        var path = Zip("mission.zip", ("MyMission/main.code", "code"));

        var result = ModFileInspector.Inspect(path, "mission.zip", installPath: "CustomMissions2");

        Assert.Equal("CustomMissions2/MyMission/main.code", Assert.Single(result.Entries).To);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("BepInEx/../../evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    public void Inspect_RejectsPathsThatLeaveTheGameFolder(string entryName)
    {
        var path = Zip("evil.zip", (entryName, "x"));

        Assert.Throws<InvalidDataException>(() => ModFileInspector.Inspect(path, "evil.zip", installPath: null));
    }

    [Fact]
    public void Inspect_RejectsTwoFilesWithTheSameTarget()
    {
        var path = Zip("twice.zip", ("BepInEx/plugins/a.txt", "1"), ("BepInEx/plugins/A.txt", "2"));

        var error = Assert.Throws<InvalidDataException>(() => ModFileInspector.Inspect(path, "twice.zip", installPath: null));
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void Inspect_RejectsAFileThatIsNotAnArchive()
    {
        var path = Write("broken.zip", "not a zip at all");

        Assert.Throws<InvalidDataException>(() => ModFileInspector.Inspect(path, "broken.zip", installPath: null));
    }

    [Fact]
    public void ListEntries_GivesSlashSeparatedFilePaths()
    {
        var path = Zip("list.zip", ("a/b.txt", "1"), ("c.txt", "2"));
        using var stream = File.OpenRead(path);

        var entries = ModFileInspector.ListEntries(stream);

        Assert.NotNull(entries);
        Assert.Equal(["a/b.txt", "c.txt"], entries.Order());
    }

    [Fact]
    public void ListEntries_IsNullForSomethingThatIsNotAnArchive()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("plain text"));

        Assert.Null(ModFileInspector.ListEntries(stream));
    }

    [Theory]
    [InlineData("BepInEx/plugins/Mod.dll", PlacementKind.Plugin)]
    [InlineData("BepInEx/plugins/Sub/Mod.DLL", PlacementKind.Plugin)]
    [InlineData("BepInEx/config/mod.cfg", PlacementKind.Config)]
    [InlineData("BepInEx/patchers/Patch.dll", PlacementKind.Other)]
    [InlineData("CustomMissions2/m/main.code", PlacementKind.Other)]
    public void ClassifyKind_DependsOnFolderAndExtension(string target, PlacementKind expected)
    {
        Assert.Equal(expected, ModFileInspector.ClassifyKind(target));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Zip(string name, params (string Entry, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
