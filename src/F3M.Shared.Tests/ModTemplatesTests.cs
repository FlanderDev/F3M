using F3M.Shared.Helpers;

namespace F3M.Shared.Tests;

/// <summary>The archive layouts below are taken from mods posted on F95zone.</summary>
public class ModTemplatesTests
{
    // ── Suggest ───────────────────────────────────────────────────────────────

    [Fact]
    public void Suggest_PlainDll_IsPlugin()
    {
        var s = ModTemplates.Suggest("Texture_Replacer_BE_il2cpp.dll", null, "Texture Replacer");
        Assert.Equal(new TemplateSuggestion(ModTemplates.BepInExPlugin, "BepInEx/plugins", ""), s);
    }

    [Fact]
    public void Suggest_GameFolderLayout_InstallsIntoGameFolder()
    {
        var s = ModTemplates.Suggest("mod.zip", ["BepInEx/plugins/TransformPlugin.dll", "CustomMissions2/Transform/main.code", "Transform-ReadMe.md"], "Transform");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "", ""), s);
    }

    [Fact]
    public void Suggest_WrappedGameFolder_UsesTheWrapperAsArchiveFolder()
    {
        var s = ModTemplates.Suggest("SFM_MapLoader.zip", ["Install/BepInEx/plugins/SFM_MapLoader.dll", "README.md"], "Map Loader");
        Assert.Equal(new TemplateSuggestion(ModTemplates.BepInExPlugin, "", "Install"), s);
    }

    [Fact]
    public void Suggest_DeeplyWrappedGameFolder_UsesTheWholePath()
    {
        const string root = "home/randy/SFM-Codex-Playground/SecretFlasherManaka v1.1.3_Uncensored";
        var s = ModTemplates.Suggest("IrlVibeDll.zip", [$"{root}/BepInEx/plugins/IrlVibes.dll"], "irlVibes");
        Assert.Equal(new TemplateSuggestion(ModTemplates.BepInExPlugin, "", root), s);
    }

    [Fact]
    public void Suggest_BepInExSubfolders_InstallIntoBepInEx()
    {
        var s = ModTemplates.Suggest("vr.zip", ["plugins/VR.dll", "config/com.codex.vr.cfg"], "VR");
        Assert.Equal(new TemplateSuggestion(ModTemplates.BepInExPlugin, "BepInEx", ""), s);
    }

    [Fact]
    public void Suggest_ConfigFolderInsideAMission_IsNotTakenForBepInEx()
    {
        var s = ModTemplates.Suggest("m.zip", ["Mission/main.code", "Mission/config/settings.json"], "Mission");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "CustomMissions2", ""), s);
    }

    [Fact]
    public void Suggest_MissionFolders_GoIntoCustomMissions2()
    {
        var s = ModTemplates.Suggest("CosplayShop.zip", ["CosplayShop/main.code"], "Cosplay Shop");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "CustomMissions2", ""), s);
    }

    [Fact]
    public void Suggest_LooseCodeFiles_GetAFolderNamedAfterTheMod()
    {
        var s = ModTemplates.Suggest("FansChat.rar", ["fanschat.code", "meta.json"], "Fans Chat: Replies?");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "CustomMissions2/Fans Chat Replies", ""), s);
    }

    [Fact]
    public void Suggest_VersionedWrapperAroundAMissionFolder_IsDropped()
    {
        var s = ModTemplates.Suggest("dropClothes_version_1.1.zip", ["dropClothes version 1.1/dropClothes/main.code"], "dropClothes");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "CustomMissions2", "dropClothes version 1.1"), s);
    }

    [Fact]
    public void Suggest_OneMissionFolderWithAReadmeBesideIt_InstallsOnlyTheFolder()
    {
        var s = ModTemplates.Suggest("Price.zip",
            ["The Price of Losing Control V0.11/readme.txt", "The Price of Losing Control V0.11/The Price of Losing Control/common.code"], "The Price of Losing Control");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV2, "CustomMissions2/The Price of Losing Control",
            "The Price of Losing Control V0.11/The Price of Losing Control"), s);
    }

    [Fact]
    public void Suggest_JsonMissions_GoIntoCustomMissions()
    {
        var s = ModTemplates.Suggest("brat.zip", ["Brat_1.json", "Brat_2.json", "readme.txt"], "Brat Missions");
        Assert.Equal(new TemplateSuggestion(ModTemplates.MissionsV1, "CustomMissions", ""), s);
    }

    [Fact]
    public void Suggest_DllWithAssets_IsPlugin()
    {
        var s = ModTemplates.Suggest("Moaning.zip", ["Moaning.dll", "Sounds/a.wav"], "Moaning");
        Assert.Equal(new TemplateSuggestion(ModTemplates.BepInExPlugin, "BepInEx/plugins", ""), s);
    }

    [Fact]
    public void Suggest_CosplayLoaderFolders_GoIntoPlugins()
    {
        var s = ModTemplates.Suggest("apron.zip",
            ["ModCosplays/Naked Apron/apron.glb", "ModCosplays/Naked Apron/description.json", "ModMaterials/Naked Apron/Naked Apron.png", "ModMaterials/Naked Apron/material.json"],
            "Naked Apron");
        Assert.Equal(new TemplateSuggestion(ModTemplates.Cosplay, "BepInEx/plugins", ""), s);
    }

    [Fact]
    public void Suggest_BareOutfitFolders_GoIntoModCosplays()
    {
        var s = ModTemplates.Suggest("outfit.7z", ["Bikini/bikini.glb", "Bikini/description.json"], "Bikini");
        Assert.Equal(new TemplateSuggestion(ModTemplates.Cosplay, "BepInEx/plugins/ModCosplays", ""), s);
    }

    [Fact]
    public void Suggest_WrappedModImagesWithPreview_UsesTheWrapper()
    {
        var s = ModTemplates.Suggest("coat.zip", ["Yellow Coat/ModImages/coat.png", "Yellow Coat/preview.jpg"], "Yellow Coat");
        Assert.Equal(new TemplateSuggestion(ModTemplates.TexturePack, "BepInEx/plugins", "Yellow Coat"), s);
    }

    [Fact]
    public void Suggest_OnlyPngs_IsTexturePack()
    {
        var s = ModTemplates.Suggest("body.zip", ["Tex_Body.png", "Nails/nails.png"], "Red nails");
        Assert.Equal(new TemplateSuggestion(ModTemplates.TexturePack, "BepInEx/plugins/ModImages", ""), s);
    }

    [Fact]
    public void Suggest_UnknownContent_IsCustom()
    {
        var s = ModTemplates.Suggest("data.zip", ["something.bin"], "Data");
        Assert.Equal(new TemplateSuggestion(ModTemplates.Custom, "", ""), s);
    }

    // ── TargetsOf ─────────────────────────────────────────────────────────────

    [Fact]
    public void TargetsOf_PlainFile_IsItsTarget() =>
        Assert.Equal(["BepInEx/plugins/A.dll"], ModTemplates.TargetsOf("A.dll", null, "", ""));

    [Fact]
    public void TargetsOf_ArchiveWithFolder_KeepsOnlyThatFolderWithoutIt() =>
        Assert.Equal(["CustomMissions2/dropClothes/main.code"],
            ModTemplates.TargetsOf("d.zip", ["dropClothes version 1.1/dropClothes/main.code", "readme.txt"], "CustomMissions2", "dropClothes version 1.1"));

    [Fact]
    public void TargetsOf_InvalidInput_IsNull()
    {
        Assert.Null(ModTemplates.TargetsOf("d.zip", ["a.code"], "../outside", ""));
        Assert.Null(ModTemplates.TargetsOf("d.zip", ["a.code"], "", "bad|folder"));
        Assert.Null(ModTemplates.TargetsOf("A.dll", null, "", "folder"));
    }

    [Fact]
    public void DefaultInstallPaths_AreTheTypesFolders()
    {
        Assert.All(ModTemplates.All.Where(t => t.Id != ModTemplates.Custom), t => Assert.False(string.IsNullOrEmpty(t.DefaultInstallPath)));
        Assert.Equal("", ModTemplates.Find(ModTemplates.Custom)!.DefaultInstallPath);
    }

    // ── Classify ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ModTemplates.MissionsV2, "BepInEx/plugins/X.dll", "CustomMissions2/X/main.code")]
    [InlineData(ModTemplates.MissionsV1, "CustomMissions/a.json")]
    [InlineData(ModTemplates.Cosplay, "BepInEx/plugins/ModMaterials/A/a.png")]
    [InlineData(ModTemplates.TexturePack, "BepInEx/plugins/ModImages/Tex_Body.png")]
    [InlineData(ModTemplates.BepInExPlugin, "BepInEx/plugins/A.dll", "BepInEx/config/a.cfg", "README.md")]
    [InlineData(ModTemplates.Custom, "BepInEx/plugins/SFM_custom_mission_v2/config.json")]
    [InlineData(ModTemplates.Custom, "BepInEx/plugins/A.dll", "Docs/guide.png")]
    public void Classify_PicksTheFolderTheFileFills(string expected, params string[] targets) =>
        Assert.Equal(expected, ModTemplates.Classify(targets));

    // ── Check ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Check_WellFormedFiles_HaveNoWarnings()
    {
        Assert.Empty(ModTemplates.Check(ModTemplates.BepInExPlugin, ["BepInEx/plugins/A.dll", "BepInEx/config/a.cfg"]));
        Assert.Empty(ModTemplates.Check(ModTemplates.MissionsV1, ["CustomMissions/a.json", "CustomMissions/Pack/b.json"]));
        Assert.Empty(ModTemplates.Check(ModTemplates.MissionsV2, ["CustomMissions2/A/main.code", "CustomMissions2/A/meta.json", "CustomMissions2/A/snd/x.ogg", "BepInEx/plugins/A.dll"]));
        Assert.Empty(ModTemplates.Check(ModTemplates.Cosplay, ["BepInEx/plugins/ModCosplays/A/a.glb", "BepInEx/plugins/ModCosplays/A/description.json", "BepInEx/plugins/ModMaterials/A/a.png"]));
        Assert.Empty(ModTemplates.Check(ModTemplates.TexturePack, ["BepInEx/plugins/ModImages/Tex_Body.png", "BepInEx/plugins/ModImages/Select/Black/hair.png"]));
    }

    [Fact]
    public void Check_Custom_NeverWarns() =>
        Assert.Empty(ModTemplates.Check(ModTemplates.Custom, ["winhttp.dll", "README.md", "anything/else.bin"]));

    [Fact]
    public void Check_UnknownType_NeverWarns() =>
        Assert.Empty(ModTemplates.Check("no-such-type", ["README.md"]));

    [Fact]
    public void Check_BundledBepInEx_Warns()
    {
        var warnings = ModTemplates.Check(ModTemplates.BepInExPlugin, ["winhttp.dll", "BepInEx/core/BepInEx.Core.dll", "BepInEx/plugins/A.dll"]);
        var warning = Assert.Single(warnings);
        Assert.Contains("BepInEx itself", warning);
        Assert.Contains("winhttp.dll", warning);
    }

    [Fact]
    public void Check_FilesInTheGameFolder_Warn()
    {
        var warnings = ModTemplates.Check(ModTemplates.BepInExPlugin, ["BepInEx/plugins/A.dll", "README.md", "CHANGELOG.md"]);
        Assert.Contains(warnings, w => w.Contains("straight into the game folder") && w.Contains("README.md, CHANGELOG.md"));
    }

    [Fact]
    public void Check_FilesOutsideTheType_Warn()
    {
        var warnings = ModTemplates.Check(ModTemplates.MissionsV1, ["CustomMissions/a.json", "~Screenshot Notes/a.png", "~Screenshot Notes/b.png", "~Screenshot Notes/c.png", "~Screenshot Notes/d.png"]);
        var warning = Assert.Single(warnings);
        Assert.Contains("4 files are outside CustomMissions", warning);
        Assert.Contains("and 1 more", warning);
    }

    [Fact]
    public void Check_PluginWithoutDll_Warns() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.BepInExPlugin, ["BepInEx/config/a.cfg"]), w => w.Contains("No .dll"));

    [Fact]
    public void Check_V1WithCodeFiles_PointsToV2() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.MissionsV1, ["CustomMissions/a.json", "CustomMissions/b.code"]), w => w.Contains("CustomMissions2"));

    [Fact]
    public void Check_V2CodeOneFolderTooDeep_Warns()
    {
        var warning = Assert.Single(ModTemplates.Check(ModTemplates.MissionsV2, ["CustomMissions2/dropClothes version 1.1/dropClothes/main.code"]));
        Assert.Contains("one folder deeper", warning);
    }

    [Fact]
    public void Check_V2FileWithoutMissionFolder_Warns()
    {
        var warnings = ModTemplates.Check(ModTemplates.MissionsV2, ["CustomMissions2/A/main.code", "CustomMissions2/jacket.txt"]);
        Assert.Contains(warnings, w => w.Contains("directly in CustomMissions2") && w.Contains("jacket.txt"));
    }

    [Fact]
    public void Check_V2FolderWithoutCode_Warns() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.MissionsV2, ["CustomMissions2/A/main.code", "CustomMissions2/Sounds/a.ogg"]), w => w.Contains("Sounds has no .code"));

    [Fact]
    public void Check_OutfitWithoutModel_Warns() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.Cosplay, ["BepInEx/plugins/ModCosplays/A/description.json"]), w => w.Contains("no .glb model"));

    [Fact]
    public void Check_TextureThatIsNotPng_Warns() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.TexturePack, ["BepInEx/plugins/ModImages/a.jpg"]), w => w.Contains("only loads .png"));

    [Fact]
    public void Check_NestedModImages_Warns() =>
        Assert.Contains(ModTemplates.Check(ModTemplates.TexturePack, ["BepInEx/plugins/ModImages/NPC 01-18/ModImages/Tex_Body.png"]), w => w.Contains("another ModImages"));

    [Fact]
    public void Check_IsCaseInsensitiveAndAcceptsBackslashes() =>
        Assert.Empty(ModTemplates.Check(ModTemplates.BepInExPlugin, [@"bepinex\Plugins\A.DLL"]));

    // ── Ids and names ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, ModTemplates.Custom)]
    [InlineData("", ModTemplates.Custom)]
    [InlineData(" Custom-Missions-V2 ", ModTemplates.MissionsV2)]
    public void TryNormalizeId_AcceptsKnownIds(string? input, string expected)
    {
        Assert.True(ModTemplates.TryNormalizeId(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeId_RejectsUnknownIds() => Assert.False(ModTemplates.TryNormalizeId("mystery", out _));

    [Fact]
    public void Ids_AreUniqueAndFitTheColumn()
    {
        Assert.Equal(ModTemplates.All.Count, ModTemplates.All.Select(t => t.Id).Distinct().Count());
        Assert.All(ModTemplates.All, t => Assert.InRange(t.Id.Length, 1, ModTemplates.MaxIdLength));
    }

    [Fact]
    public void Names_CanBeTagNames() =>
        Assert.All(ModTemplates.All, t => Assert.Null(TagRules.NameProblem(TagRules.Clean(t.Name))));

    // ── TypeIdsOf (the type tags a mod gets) ─────────────────────────────────

    [Fact]
    public void TypeIdsOf_TheFilesTypes_EachOnceWithoutCustom()
    {
        var ids = ModTemplates.TypeIdsOf([ModTemplates.MissionsV2, ModTemplates.Custom, ModTemplates.BepInExPlugin, ModTemplates.MissionsV2]);
        Assert.Equal([ModTemplates.MissionsV2, ModTemplates.BepInExPlugin], ids);
    }

    [Fact]
    public void TypeIdsOf_TypedFiles_IgnoreTheCategory()
    {
        Assert.Equal([ModTemplates.MissionsV1], ModTemplates.TypeIdsOf([ModTemplates.MissionsV1], "BepInEx-Plugin"));
    }

    [Fact]
    public void TypeIdsOf_NoTypedFiles_TakeTheTypeTheCategoryNames()
    {
        Assert.Equal([ModTemplates.BepInExPlugin], ModTemplates.TypeIdsOf([ModTemplates.Custom], " bepinex-plugin "));
    }

    [Theory]
    [InlineData("Others")]
    [InlineData("Unspecified")]
    [InlineData(null)]
    public void TypeIdsOf_NoTypedFilesNorTypeCategory_IsNone(string? category)
    {
        Assert.Empty(ModTemplates.TypeIdsOf([ModTemplates.Custom, "unknown-type"], category));
    }

    [Theory]
    [InlineData("Yuu's Missions", "Yuu's Missions")]
    [InlineData("Fans Chat: Replies?", "Fans Chat Replies")]
    [InlineData("失踪人口案件", "失踪人口案件")]
    [InlineData("...", "Mod")]
    [InlineData("CON", "Mod")]
    [InlineData(null, "Mod")]
    public void SafeFolderName_KeepsWhatAFolderMayHold(string? input, string expected) =>
        Assert.Equal(expected, ModTemplates.SafeFolderName(input));
}
