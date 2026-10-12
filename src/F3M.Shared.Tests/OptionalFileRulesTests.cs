using System.Text.Json;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Shared.Tests;

/// <summary>Optional groups: validation, matching, what a version installs, and the suggestions from the mod types.</summary>
public class OptionalFileRulesTests
{
    private static OptionalGroupDto Group(string name, params string[] patterns) => new() { Name = name, Patterns = [.. patterns] };

    private static List<OptionalGroupDto> Normalized(params OptionalGroupDto[] groups)
    {
        Assert.True(OptionalFileRules.TryNormalize(groups, out var normalized, out var error), error);
        return normalized;
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("  Examples ", "Examples")]
    [InlineData("Example\t missions", "Example missions")]
    public void TryNormalizeName_TrimsAndCollapsesWhitespace(string input, string expected)
    {
        Assert.True(OptionalFileRules.TryNormalizeName(input, out var name, out _));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("A name that is much longer than forty characters")]
    public void TryNormalizeName_RejectsEmptyAndLongNames(string? input)
    {
        Assert.False(OptionalFileRules.TryNormalizeName(input, out _, out var error));
        Assert.NotNull(error);
    }

    // ── Groups ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryNormalize_NormalizesPatterns()
    {
        var groups = Normalized(Group(" Examples ", @"CustomMissions2\Pack\Examples\**", "./README.md"));
        Assert.Equal("Examples", groups[0].Name);
        Assert.Equal(["CustomMissions2/Pack/Examples/**", "README.md"], groups[0].Patterns);
    }

    [Fact]
    public void TryNormalize_RejectsTwoGroupsWithTheSameName()
    {
        Assert.False(OptionalFileRules.TryNormalize([Group("Examples", "a/**"), Group("examples", "b/**")], out _, out var error));
        Assert.Contains("two optional groups", error);
    }

    [Fact]
    public void TryNormalize_RejectsAGroupWithoutPatterns()
    {
        Assert.False(OptionalFileRules.TryNormalize([Group("Examples")], out _, out var error));
        Assert.Contains("has no files", error);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute/path")]
    [InlineData("C:/Games/x.txt")]
    [InlineData("**")]
    [InlineData("**/*")]
    [InlineData("a**b/c")]
    public void TryNormalize_RejectsInvalidPatterns(string pattern)
    {
        Assert.False(OptionalFileRules.TryNormalize([Group("Examples", pattern)], out _, out var error));
        Assert.StartsWith("Optional group 'Examples':", error);
    }

    [Fact]
    public void TryNormalize_ErrorsNameTheOptionalPattern()
    {
        Assert.False(OptionalFileRules.TryNormalize([Group("Examples", "  ")], out _, out var error));
        Assert.Equal("Optional group 'Examples': A pattern cannot be empty.", error);
    }

    [Fact]
    public void TryNormalize_RejectsAPatternInTwoGroups()
    {
        Assert.False(OptionalFileRules.TryNormalize([Group("A", "Docs/**"), Group("B", "docs/**")], out _, out var error));
        Assert.Contains("more than once", error);
    }

    [Fact]
    public void TryNormalize_LimitsGroupsAndPatterns()
    {
        var many = Enumerable.Range(0, OptionalFileRules.MaxGroupsPerVersion + 1).Select(i => Group($"G{i}", $"f{i}.txt")).ToList();
        Assert.False(OptionalFileRules.TryNormalize(many, out _, out _));

        var patterns = Enumerable.Range(0, OptionalFileRules.MaxPatternsPerGroup + 1).Select(i => $"f{i}.txt").ToArray();
        Assert.False(OptionalFileRules.TryNormalize([Group("Big", patterns)], out _, out _));
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    [Fact]
    public void GroupOf_FirstMatchingGroupWins()
    {
        var groups = Normalized(Group("Documentation", "CustomMissions2/Pack/Examples/README.md"), Group("Examples", "CustomMissions2/Pack/Examples/**"));

        Assert.Equal("Documentation", OptionalFileRules.GroupOf(groups, "CustomMissions2/Pack/Examples/README.md"));
        Assert.Equal("Examples", OptionalFileRules.GroupOf(groups, "customMissions2/pack/examples/deep/a.code"));
        Assert.Null(OptionalFileRules.GroupOf(groups, "CustomMissions2/Pack/main.code"));
    }

    [Fact]
    public void IsInstalled_OptionalFilesOnlyWhenTheirGroupIsOn()
    {
        IReadOnlyList<OptionalGroupRef> groups = [new("Examples", ["CustomMissions2/**"])];

        Assert.True(OptionalFileRules.IsInstalled(groups, "BepInEx/plugins/SFM_custom_mission_v2.dll", null));
        Assert.False(OptionalFileRules.IsInstalled(groups, "CustomMissions2/CM1 Emulator/main.code", null));
        Assert.False(OptionalFileRules.IsInstalled(groups, "CustomMissions2/CM1 Emulator/main.code", ["Documentation"]));
        Assert.True(OptionalFileRules.IsInstalled(groups, "CustomMissions2/CM1 Emulator/main.code", ["examples"]));
        Assert.True(OptionalFileRules.IsInstalled(null, "CustomMissions2/CM1 Emulator/main.code", null));
    }

    // ── Analyse ───────────────────────────────────────────────────────────────

    private static readonly string[] FrameworkV2 =
    [
        "BepInEx/plugins/SFM_custom_mission_v2.dll",
        "BepInEx/plugins/SFM_custom_mission_v2/config.json",
        "BepInEx/plugins/SFM_custom_mission_v2/documentation.html",
        "CustomMissions2/CM1 Emulator/main.code",
        "CustomMissions2/CM1 Emulator/engine.code",
        "CustomMissions2/drop bra and panties/main.code",
        "CustomMissions2/Examples.zip",
    ];

    [Fact]
    public void Analyse_CountsFilesPerGroup()
    {
        var report = OptionalFileRules.Analyse(Normalized(Group("Example missions", "CustomMissions2/**")), FrameworkV2);

        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
        Assert.Equal(4, report.FilesPerGroup["Example missions"]);
        Assert.Equal(3, report.Installed);
    }

    [Fact]
    public void Analyse_EverythingOptionalIsAnError()
    {
        var report = OptionalFileRules.Analyse(Normalized(Group("All", "BepInEx/**", "CustomMissions2/**")), FrameworkV2);
        Assert.Single(report.Errors);
        Assert.Equal(0, report.Installed);
    }

    [Fact]
    public void Analyse_WarnsAboutGroupsAndPatternsThatMatchNothing()
    {
        var report = OptionalFileRules.Analyse(
            Normalized(Group("Examples", "CustomMissions2/**", "Tutorials/**"), Group("Docs", "README.md")),
            FrameworkV2);

        Assert.Empty(report.Errors);
        Assert.Equal(
            ["'Tutorials/**' in the optional group 'Examples' matches none of the files.", "The optional group 'Docs' matches none of the files."],
            report.Warnings);
    }

    [Fact]
    public void Analyse_NoGroupsInstallEverything()
    {
        var report = OptionalFileRules.Analyse([], FrameworkV2);
        Assert.Empty(report.Errors);
        Assert.Equal(FrameworkV2.Length, report.Installed);
    }

    [Theory]
    [InlineData("CustomMissions2/Pack/Examples", true, "CustomMissions2/Pack/Examples/**")]
    [InlineData("CustomMissions2/Pack/Examples/", true, "CustomMissions2/Pack/Examples/**")]
    [InlineData("README.md", false, "README.md")]
    public void PatternFor_FileOrFolder(string path, bool isFolder, string expected) =>
        Assert.Equal(expected, OptionalFileRules.PatternFor(path, isFolder));

    // ── Suggestions (layouts from mods posted on F95zone) ─────────────────────

    [Fact]
    public void Suggest_ReadmeNextToAMission_IsDocumentation()
    {
        var groups = ModTemplates.SuggestOptionalGroups(["BepInEx/plugins/TransformPlugin.dll", "CustomMissions2/Transform/main.code", "Transform-ReadMe.md"]);
        var group = Assert.Single(groups);
        Assert.Equal(ModTemplates.DocumentationGroup, group.Name);
        Assert.Equal(["Transform-ReadMe.md"], group.Patterns);
    }

    [Fact]
    public void Suggest_LicencesWithoutExtension_AreDocumentation()
    {
        var groups = ModTemplates.SuggestOptionalGroups(["BepInEx/plugins/SecretFlasherManakaVR.dll", "LICENSE", "THIRD_PARTY_NOTICES.md"]);
        Assert.Equal(["LICENSE", "THIRD_PARTY_NOTICES.md"], Assert.Single(groups).Patterns);
    }

    [Fact]
    public void Suggest_ReadMeWithSpaces_IsDocumentation()
    {
        var groups = ModTemplates.SuggestOptionalGroups(["CustomMissions/SekiMissions.json", "SekiMissions Read Me.txt"]);
        Assert.Equal(["SekiMissions Read Me.txt"], Assert.Single(groups).Patterns);
    }

    [Fact]
    public void Suggest_FrameworkV2_DocumentationAndExamples()
    {
        var groups = ModTemplates.SuggestOptionalGroups(FrameworkV2);

        Assert.Equal([ModTemplates.DocumentationGroup, ModTemplates.ExamplesGroup], groups.Select(g => g.Name));
        Assert.Equal(["BepInEx/plugins/SFM_custom_mission_v2/documentation.html"], groups[0].Patterns);
        Assert.Equal(["CustomMissions2/Examples.zip"], groups[1].Patterns);
    }

    [Fact]
    public void Suggest_ExampleFolders_BecomeFolderPatterns()
    {
        var groups = ModTemplates.SuggestOptionalGroups(
        [
            "CustomMissions2/Pack/main.code",
            "CustomMissions2/Pack/Examples/one.code",
            "CustomMissions2/Pack/Examples/two/two.code",
            "CustomMissions2/ExampleMission/main.code",
            "CustomMissions2/my_demo/main.code",
            "CustomMissions2/Pack/tutorial.zip",
        ]);

        var examples = Assert.Single(groups);
        Assert.Equal(
            ["CustomMissions2/Pack/Examples/**", "CustomMissions2/ExampleMission/**", "CustomMissions2/my_demo/**", "CustomMissions2/Pack/tutorial.zip"],
            examples.Patterns);
    }

    [Theory]
    [InlineData("CustomMissions2/Demolition/main.code")]
    [InlineData("CustomMissions2/Counterexample/main.code")]
    [InlineData("CustomMissions2/Pack/readme.code")]
    [InlineData("CustomMissions2/Pack/license.json")]
    [InlineData("CustomMissions2/jacket.txt")]
    [InlineData("CustomMissions/testmission.json")]
    [InlineData("CustomMissions/cc_sample.json")] // Conbini Climax: a mission called "Free Sample"
    [InlineData("CustomMissions2/Pack/tutorial.code")]
    [InlineData("BepInEx/plugins/ModImages/sample_texture.png")]
    public void Suggest_LeavesModFilesAlone(string path)
    {
        Assert.Empty(ModTemplates.SuggestOptionalGroups(["BepInEx/plugins/Mod.dll", path]));
    }

    [Fact]
    public void Suggest_NeverMakesEverythingOptional()
    {
        Assert.Empty(ModTemplates.SuggestOptionalGroups(["README.md", "LICENSE"]));
        Assert.Empty(ModTemplates.SuggestOptionalGroups(["CustomMissions2/Examples/main.code", "CustomMissions2/Examples/readme.txt"]));
    }

    [Fact]
    public void Suggest_DoesNotRepeatDocumentationAsExamples()
    {
        var groups = ModTemplates.SuggestOptionalGroups(["CustomMissions2/Pack/main.code", "CustomMissions2/Pack/Examples/README.md"]);
        Assert.Equal(["CustomMissions2/Pack/Examples/README.md"], Assert.Single(groups).Patterns);
    }

    [Fact]
    public void SuggestAdditional_KeepsTheAuthorsGroupsFirst()
    {
        // The import plan names the framework's example missions; the readme there is already in that group.
        string[] targets = [.. FrameworkV2, "CustomMissions2/readme.txt"];
        var added = ModTemplates.SuggestOptionalGroups([Group("Example missions", "CustomMissions2/**")], targets);

        var documentation = Assert.Single(added);
        Assert.Equal(ModTemplates.DocumentationGroup, documentation.Name);
        Assert.Equal(["BepInEx/plugins/SFM_custom_mission_v2/documentation.html"], documentation.Patterns);
    }

    [Fact]
    public void SuggestAdditional_SkipsUsedAndDismissedNames()
    {
        string[] targets = ["CustomMissions2/Transform/main.code", "README.md", "CustomMissions2/Transform/Examples/a.code"];

        var withOwnDocs = ModTemplates.SuggestOptionalGroups([Group("documentation", "CustomMissions2/Transform/Examples/a.code")], targets);
        Assert.DoesNotContain(withOwnDocs, g => g.Name == ModTemplates.DocumentationGroup);

        var dismissed = ModTemplates.SuggestOptionalGroups([], targets, [ModTemplates.ExamplesGroup]);
        Assert.Equal([ModTemplates.DocumentationGroup], dismissed.Select(g => g.Name));
    }

    [Fact]
    public void SuggestAdditional_NothingWhenTheResultWouldInstallNothing()
    {
        string[] targets = ["CustomMissions2/Pack/main.code", "README.md"];
        Assert.Empty(ModTemplates.SuggestOptionalGroups([Group("Everything", "CustomMissions2/**")], targets));
    }

    [Fact]
    public void SuggestAdditional_NothingWhenTheGivenGroupsAreInvalid()
    {
        Assert.Empty(ModTemplates.SuggestOptionalGroups([Group("Broken", "../x")], ["a.dll", "README.md"]));
    }

    // ── Type checks only see installed files ──────────────────────────────────

    [Fact]
    public void Check_OptionalReadmeInTheGameFolder_IsNotWarnedAbout()
    {
        string[] targets = ["CustomMissions2/Transform/main.code", "Transform-ReadMe.md"];
        Assert.NotEmpty(ModTemplates.Check(ModTemplates.MissionsV2, targets));

        var groups = ModTemplates.SuggestOptionalGroups(targets);
        Assert.Empty(ModTemplates.Check(ModTemplates.MissionsV2, OptionalFileRules.Installed(groups, targets)));
    }

    // ── Catalog documents ─────────────────────────────────────────────────────

    [Fact]
    public void VersionDocument_WithoutOptionalGroups_StillReads()
    {
        const string json = """
            {"schema":1,"groupId":1,"versionId":2,"version":"1.0","name":"M","author":"A","description":"","category":"C",
             "publishedAt":"2026-01-01T00:00:00Z","previewUrl":null,"files":[],"dependencies":[],"generatedPaths":[]}
            """;
        var doc = JsonSerializer.Deserialize<VersionDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(doc.OptionalGroups);
    }

    [Fact]
    public void VersionDocument_OptionalGroups_RoundTrip()
    {
        var doc = new VersionDocument(1, 1, 2, "1.0", "M", "A", "", "C", DateTime.UnixEpoch, null, [], [], [], [new("Examples", ["CustomMissions2/**"])]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(doc, options);

        Assert.Contains("\"optionalGroups\":[{\"name\":\"Examples\",\"patterns\":[\"CustomMissions2/**\"]}]", json);
        Assert.Equal("Examples", JsonSerializer.Deserialize<VersionDocument>(json, options)!.OptionalGroups![0].Name);
    }
}
