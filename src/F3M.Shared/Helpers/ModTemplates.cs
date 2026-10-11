namespace F3M.Shared.Helpers;

/// <summary>One kind of mod file: where its files belong in the game folder and what it needs to work.</summary>
/// <param name="Id">Stored on <see cref="Models.ModFile.Template"/>; never changes once used.</param>
/// <param name="Category">The category an upload of this type gets unless the author picks another.</param>
/// <param name="Folders">Game-relative folders this type installs into; files elsewhere get a warning. Empty: anywhere.</param>
/// <param name="RequiredModName">The mod players need for this type to work, by its name on the site; null for none.</param>
/// <param name="DefaultInstallPath">The install path the upload form offers when the author picks this type.</param>
public sealed record ModTemplate(
    string Id,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<string> Folders,
    string? RequiredModName,
    string DefaultInstallPath);

/// <summary>What <see cref="ModTemplates.Suggest"/> derives from a file: its type, install path and archive folder.</summary>
public sealed record TemplateSuggestion(string TemplateId, string InstallPath, string ArchiveRoot);

/// <summary>
/// The kinds of mods for Secret Flasher Manaka and the rules for each. Shared by the upload form (suggestions and the
/// preview) and the server (which checks every upload again), so both report the same things.
///
/// Every rule produces a warning, never an error: authors know their mod better than these rules do. The server
/// stores an upload with warnings only once the uploader has confirmed them.
/// </summary>
public static class ModTemplates
{
    public const string Custom = "custom";
    public const string BepInExPlugin = "bepinex-plugin";
    public const string MissionsV1 = "custom-missions-v1";
    public const string MissionsV2 = "custom-missions-v2";
    public const string Cosplay = "cosplay";
    public const string TexturePack = "texture-pack";

    /// <summary>Matches the length of the database column.</summary>
    public const int MaxIdLength = 40;

    public const string PluginsFolder = InstallPaths.DefaultPluginDirectory;
    public const string ConfigFolder = "BepInEx/config";
    public const string PatchersFolder = "BepInEx/patchers";
    public const string MissionsV1Folder = "CustomMissions";
    public const string MissionsV2Folder = "CustomMissions2";
    public const string CosplaysFolder = "BepInEx/plugins/ModCosplays";
    public const string MaterialsFolder = "BepInEx/plugins/ModMaterials";
    public const string ImagesFolder = "BepInEx/plugins/ModImages";

    /// <summary>At most this many paths are named in one warning.</summary>
    private const int MaxExamples = 3;

    // Missions often ship a helper plugin of their own next to the mission files.
    private static readonly string[] PluginFolders = [PluginsFolder, ConfigFolder, PatchersFolder];

    public static readonly IReadOnlyList<ModTemplate> All =
    [
        new(BepInExPlugin, "BepInEx plugin", "BepInEx-Plugin",
            ".dll plugins in BepInEx/plugins, with their settings in BepInEx/config.",
            PluginFolders, null, PluginsFolder),
        new(MissionsV1, "Custom Missions v1", "Custom-Missions 1",
            "Missions as .json files in the CustomMissions folder. Needs the Custom Missions v1 framework.",
            [MissionsV1Folder, .. PluginFolders], "Custom Missions v1", MissionsV1Folder),
        new(MissionsV2, "Custom Missions v2", "Custom-Missions 2",
            "Missions as folders in CustomMissions2, each with its .code files and optionally a meta.json. " +
            "Needs the Custom Missions v2 framework.",
            [MissionsV2Folder, .. PluginFolders], "Custom Missions v2", MissionsV2Folder),
        new(Cosplay, "Cosplay", "Cosplay-Loader",
            "Outfits for the Cosplay Loader: a folder per outfit in BepInEx/plugins/ModCosplays (.glb model and " +
            ".json description) and its textures in BepInEx/plugins/ModMaterials (.png and .json).",
            [CosplaysFolder, MaterialsFolder], "Cosplay Loader Mod", PluginsFolder),
        new(TexturePack, "Texture pack", "Texture Edits",
            ".png textures in BepInEx/plugins/ModImages, named after the game texture they replace. Sets to switch " +
            "between with F12 go in ModImages/Select/<set name>. Needs Texture Replacer.",
            [ImagesFolder], "Texture Replacer", ImagesFolder),
        new(Custom, "Custom", "Others",
            "Anything else. You choose the install path; no type rules are checked.",
            [], null, string.Empty),
    ];

    private static readonly HashSet<string> BepInExOwnFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "winhttp.dll", "doorstop_config.ini", ".doorstop_version",
    };

    private static readonly HashSet<string> GameRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "BepInEx", MissionsV1Folder, MissionsV2Folder,
    };

    private static readonly HashSet<string> BepInExFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "plugins", "config", "patchers",
    };

    private static readonly HashSet<string> LoaderFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModCosplays", "ModMaterials", "ModImages",
    };

    private static readonly string[] DocumentExtensions = [".txt", ".md", ".pdf", ".url", ".html", ".htm", ".rtf"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static readonly string[] DocumentNames = ["readme", "license", "licence", "changelog", "third_party_notices"];

    public static ModTemplate? Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A known template id in its stored spelling. Empty or missing means <see cref="Custom"/>.</summary>
    public static bool TryNormalizeId(string? id, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            normalized = Custom;
            return true;
        }

        normalized = Find(id.Trim())?.Id ?? string.Empty;
        return normalized.Length > 0;
    }

    /// <summary>
    /// Hints about one file of the given type, from the game-relative paths it installs ('/' separated, as the server
    /// stores them). Empty when everything looks right, and always for <see cref="Custom"/> or an unknown type.
    /// </summary>
    public static IReadOnlyList<string> Check(string? templateId, IReadOnlyCollection<string> targets)
    {
        var template = Find(templateId);
        var warnings = new List<string>();
        if (template is null || template.Id == Custom || targets.Count == 0)
            return warnings;

        var paths = targets.Select(t => t.Replace('\\', '/').Trim('/')).Where(t => t.Length > 0).ToList();

        // ── Rules for every type ──
        var bepinex = paths.Where(p => (!p.Contains('/') && BepInExOwnFiles.Contains(p))
                                       || StartsWithFolder(p, "BepInEx/core") || StartsWithFolder(p, "dotnet")).ToList();
        if (bepinex.Count > 0)
            warnings.Add($"Contains BepInEx itself ({Examples(bepinex)}). Players install BepInEx on their own; a bundled copy can replace theirs.");

        var game = paths.Where(p => StartsWithFolder(p, "SecretFlasherManaka_Data")
                                    || (!p.Contains('/') && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))).ToList();
        if (game.Count > 0)
            warnings.Add($"Changes the game's own files ({Examples(game)}).");

        var loose = paths.Where(p => !p.Contains('/')).Except(bepinex).Except(game).ToList();
        if (loose.Count > 0)
            warnings.Add($"Puts {Count(loose.Count, "file")} straight into the game folder ({Examples(loose)}). Documentation is better left out or kept in the mod's own folder.");

        var outside = paths.Where(p => p.Contains('/') && !template.Folders.Any(f => StartsWithFolder(p, f)))
            .Except(bepinex).Except(game).ToList();
        if (outside.Count > 0)
            warnings.Add($"{Count(outside.Count, "file")} {(outside.Count == 1 ? "is" : "are")} outside {Folders(template)} ({Examples(outside)}). Is this the right type?");

        // ── Rules of the type ──
        switch (template.Id)
        {
            case BepInExPlugin:
                if (!paths.Any(p => IsDll(p) && (StartsWithFolder(p, PluginsFolder) || StartsWithFolder(p, PatchersFolder))))
                    warnings.Add("No .dll is installed to BepInEx/plugins, so BepInEx has no plugin to load.");
                break;

            case MissionsV1:
                CheckMissionsV1(paths, warnings);
                break;

            case MissionsV2:
                CheckMissionsV2(paths, warnings);
                break;

            case Cosplay:
                CheckCosplay(paths, warnings);
                break;

            case TexturePack:
                CheckTextures(paths, warnings);
                break;
        }

        return warnings;
    }

    /// <summary>
    /// The type that best describes what a file installs: the mission or loader folder it fills, else a plugin when
    /// it only installs into BepInEx, else <see cref="Custom"/>. Readmes and licences are not counted.
    /// </summary>
    public static string Classify(IReadOnlyCollection<string> targets)
    {
        var paths = targets.Select(t => t.Replace('\\', '/').Trim('/')).Where(t => !IsDocument(Path.GetFileName(t))).ToList();
        if (paths.Any(p => StartsWithFolder(p, MissionsV2Folder)))
            return MissionsV2;
        if (paths.Any(p => StartsWithFolder(p, MissionsV1Folder)))
            return MissionsV1;
        if (paths.Any(p => StartsWithFolder(p, CosplaysFolder) || StartsWithFolder(p, MaterialsFolder)))
            return Cosplay;
        if (paths.Any(p => StartsWithFolder(p, ImagesFolder)))
            return TexturePack;
        if (paths.Count > 0 && paths.Any(IsDll) && paths.All(p => PluginFolders.Any(f => StartsWithFolder(p, f))))
            return BepInExPlugin;
        return Custom;
    }

    /// <summary>
    /// Where a file most likely belongs. <paramref name="archiveEntries"/> are the file paths inside an archive (null
    /// for a plain file). Recognises archives packed from the game folder, from BepInEx or a loader folder, with an
    /// extra folder around any of those, and bare mission, plugin, cosplay and texture files.
    /// </summary>
    /// <param name="modName">Names the mission folder when .code files are not in a folder of their own.</param>
    public static TemplateSuggestion Suggest(string fileName, IReadOnlyCollection<string>? archiveEntries, string? modName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        if (archiveEntries is null || !InstallPaths.IsArchive(name))
        {
            return IsDll(name)
                ? new TemplateSuggestion(BepInExPlugin, PluginsFolder, string.Empty)
                : new TemplateSuggestion(Custom, string.Empty, string.Empty);
        }

        var entries = archiveEntries
            .Select(e => InstallPaths.TrySplit(e, out var parts, out _) ? parts : [])
            .Where(parts => parts.Count > 0)
            .ToList();
        if (entries.Count == 0)
            return new TemplateSuggestion(Custom, string.Empty, string.Empty);

        // Readmes and licences say little about where the mod goes.
        var payload = entries.Where(e => !IsDocument(e[^1])).ToList();
        if (payload.Count == 0)
            payload = entries;

        var (root, installPath) = FindLayout(payload, entries, string.IsNullOrWhiteSpace(modName) ? Path.GetFileNameWithoutExtension(name) : modName);
        var targets = TargetsOf(name, entries.Select(e => string.Join('/', e)).ToList(), installPath, root) ?? [];
        return new TemplateSuggestion(Classify(targets), installPath, root);
    }

    /// <summary>
    /// The game-relative paths a file installs, the way the server stores them: a plain file's target, or each archive
    /// entry inside <paramref name="archiveRoot"/> below the extract folder. Entries with a path that is not allowed
    /// are left out. Null when the install path or the archive folder is not valid.
    /// </summary>
    /// <param name="archiveEntries">The file paths inside an archive; ignored for plain files.</param>
    public static List<string>? TargetsOf(string fileName, IReadOnlyCollection<string>? archiveEntries, string? installPath, string? archiveRoot)
    {
        if (!InstallPaths.TryPlan(fileName, installPath, out var placement) || !InstallPaths.TrySplit(archiveRoot, out var rootParts, out _))
            return null;

        if (!placement.IsArchive)
            return rootParts.Count == 0 ? [placement.Target] : null;

        var targets = new List<string>();
        foreach (var entry in archiveEntries ?? [])
        {
            if (!InstallPaths.TrySplit(entry, out var parts, out _) || parts.Count == 0)
                continue;

            var relative = parts;
            if (rootParts.Count > 0 && !InstallPaths.TryStripRoot(rootParts, parts, out relative))
                continue;

            if (InstallPaths.TryExpand(placement.Target, string.Join('/', relative), out var target))
                targets.Add(target);
        }

        return targets;
    }

    /// <summary>A folder name for a mission named after the mod: what a path segment may hold, never empty.</summary>
    public static string SafeFolderName(string? name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Where(c => !char.IsControl(c) && c is not (':' or '*' or '?' or '"' or '<' or '>' or '|' or '/' or '\\'))
            .ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 60)
            cleaned = cleaned[..60].TrimEnd('.', ' ');
        return cleaned.Length == 0 || !InstallPaths.TrySplit(cleaned, out var parts, out _) || parts.Count != 1 ? "Mod" : cleaned;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>The archive folder to install and where it goes, for the files that make up the mod.</summary>
    /// <param name="payload">The files that make up the mod: <paramref name="entries"/> without readmes and licences.</param>
    private static (string Root, string InstallPath) FindLayout(List<List<string>> payload, List<List<string>> entries, string? modName)
    {
        // A file the BepInEx loader puts next to the game exe means the archive is the game folder.
        if (payload.Any(e => e.Count == 1 && BepInExOwnFiles.Contains(e[0])))
            return (string.Empty, string.Empty);

        // Known folders, possibly inside wrapper folders: the game folder's own, then BepInEx's and the loaders'. Those
        // names are common enough inside missions (a "config" folder) that they only count when they hold everything.
        foreach (var (folders, installPath, holdsAll) in new[]
                 {
                     (GameRootFolders, string.Empty, false),
                     (BepInExFolders, "BepInEx", true),
                     (LoaderFolders, PluginsFolder, true),
                 })
        {
            if (FindMarker(payload, folders, holdsAll) is { } root)
                return (root, installPath);
        }

        static string Extension(List<string> e) => Path.GetExtension(e[^1]).ToLowerInvariant();

        // Mission v2 projects: .code files, in a folder per project.
        var code = payload.Where(e => Extension(e) == ".code").ToList();
        if (code.Count > 0)
        {
            var depth = code.Min(e => e.Count);
            if (depth == 1)
                return (string.Empty, $"{MissionsV2Folder}/{SafeFolderName(modName)}");

            // Folders around the project folders are dropped, as long as all projects sit in the same one.
            var wrapper = code.First(e => e.Count == depth).Take(depth - 2).ToList();
            var shared = code.All(e => e.Count >= depth && e.Take(depth - 2).SequenceEqual(wrapper, StringComparer.OrdinalIgnoreCase));
            if (!shared)
                return (string.Empty, MissionsV2Folder);

            // One mission with other files around it (a readme next to the folder): just the mission folder is installed.
            var project = code.First(e => e.Count == depth)[depth - 2];
            bool InProject(List<string> e) => e.Count >= depth
                                             && e.Take(depth - 2).SequenceEqual(wrapper, StringComparer.OrdinalIgnoreCase)
                                             && string.Equals(e[depth - 2], project, StringComparison.OrdinalIgnoreCase);
            if (code.All(InProject) && !entries.All(InProject))
                return (string.Join('/', wrapper.Append(project)), $"{MissionsV2Folder}/{project}");

            return (string.Join('/', wrapper), MissionsV2Folder);
        }

        if (payload.Any(e => IsDll(e[^1])))
            return (string.Empty, PluginsFolder);

        if (payload.Any(e => Extension(e) == ".glb"))
        {
            return payload.Any(e => e.Count == 1 && Extension(e) == ".glb")
                ? (string.Empty, $"{CosplaysFolder}/{SafeFolderName(modName)}")
                : (string.Empty, CosplaysFolder);
        }

        if (payload.Any(e => Extension(e) == ".json"))
            return (string.Empty, MissionsV1Folder);

        if (payload.All(e => Extension(e) == ".png"))
            return (string.Empty, ImagesFolder);

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// The folders around the first of <paramref name="folders"/> in each path, when every path that has one has it
    /// below the same folders. Null when no path has one, when they disagree, or with <paramref name="holdsAll"/> when
    /// a path other than a preview image or a readme has none.
    /// </summary>
    private static string? FindMarker(List<List<string>> payload, HashSet<string> folders, bool holdsAll)
    {
        string? found = null;
        foreach (var entry in payload)
        {
            // The file name itself never counts, only folders.
            var index = entry.FindIndex(0, entry.Count - 1, folders.Contains);
            if (index < 0)
            {
                if (holdsAll && !IsImage(entry[^1]) && !IsDocument(entry[^1]))
                    return null;
                continue;
            }

            var prefix = string.Join('/', entry.Take(index));
            if (found is null)
                found = prefix;
            else if (!string.Equals(found, prefix, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return found;
    }

    // ── Type rules ────────────────────────────────────────────────────────────

    private static void CheckMissionsV1(List<string> paths, List<string> warnings)
    {
        var missions = paths.Where(p => StartsWithFolder(p, MissionsV1Folder)).ToList();
        if (!missions.Any(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("No .json mission is installed to CustomMissions.");

        var code = missions.Where(p => p.EndsWith(".code", StringComparison.OrdinalIgnoreCase)).ToList();
        if (code.Count > 0)
            warnings.Add($"CustomMissions contains .code files ({Examples(code)}), which are Custom Missions v2 code. v2 missions go in CustomMissions2.");
    }

    private static void CheckMissionsV2(List<string> paths, List<string> warnings)
    {
        var missions = paths.Where(p => StartsWithFolder(p, MissionsV2Folder)).Select(p => p[(MissionsV2Folder.Length + 1)..]).ToList();
        if (missions.Count == 0)
        {
            warnings.Add("Nothing is installed to CustomMissions2.");
            return;
        }

        var loose = missions.Where(p => !p.Contains('/')).ToList();
        if (loose.Count > 0)
            warnings.Add($"{Count(loose.Count, "file")} {(loose.Count == 1 ? "is" : "are")} directly in CustomMissions2 ({Examples(loose.Select(p => $"{MissionsV2Folder}/{p}"))}). Each mission needs a folder of its own.");

        foreach (var project in missions.Where(p => p.Contains('/')).GroupBy(p => p[..p.IndexOf('/')], StringComparer.OrdinalIgnoreCase))
        {
            var inside = project.Select(p => p[(project.Key.Length + 1)..]).ToList();
            if (inside.Any(p => !p.Contains('/') && p.EndsWith(".code", StringComparison.OrdinalIgnoreCase)))
                continue;

            var deeper = inside.Where(p => p.EndsWith(".code", StringComparison.OrdinalIgnoreCase)).ToList();
            warnings.Add(deeper.Count > 0
                ? $"{MissionsV2Folder}/{project.Key} has its .code files one folder deeper ({Examples(deeper.Select(p => $"{project.Key}/{p}"))}). The framework reads them from the mission folder itself."
                : $"{MissionsV2Folder}/{project.Key} has no .code file, so it is not a mission.");
        }
    }

    private static void CheckCosplay(List<string> paths, List<string> warnings)
    {
        var outfits = paths.Where(p => StartsWithFolder(p, CosplaysFolder)).Select(p => p[(CosplaysFolder.Length + 1)..]).ToList();
        var materials = paths.Where(p => StartsWithFolder(p, MaterialsFolder)).Select(p => p[(MaterialsFolder.Length + 1)..]).ToList();
        if (outfits.Count == 0 && materials.Count == 0)
        {
            warnings.Add("Nothing is installed to ModCosplays or ModMaterials.");
            return;
        }

        var loose = outfits.Where(p => !p.Contains('/')).Select(p => $"ModCosplays/{p}")
            .Concat(materials.Where(p => !p.Contains('/')).Select(p => $"ModMaterials/{p}")).ToList();
        if (loose.Count > 0)
            warnings.Add($"{Examples(loose)} {(loose.Count == 1 ? "is" : "are")} not in an outfit folder. The Cosplay Loader reads one folder per outfit.");

        foreach (var outfit in outfits.Where(p => p.Contains('/')).GroupBy(p => p[..p.IndexOf('/')], StringComparer.OrdinalIgnoreCase))
        {
            var hasModel = outfit.Any(p => p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            var hasJson = outfit.Any(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (!hasModel || !hasJson)
                warnings.Add($"ModCosplays/{outfit.Key} has no {(hasModel ? ".json description" : hasJson ? ".glb model" : ".glb model or .json description")}.");
        }
    }

    private static void CheckTextures(List<string> paths, List<string> warnings)
    {
        var images = paths.Where(p => StartsWithFolder(p, ImagesFolder)).ToList();
        if (images.Count == 0)
        {
            warnings.Add("Nothing is installed to ModImages.");
            return;
        }

        var other = images.Where(p => !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        if (other.Count > 0)
            warnings.Add($"Texture Replacer only loads .png files; {Count(other.Count, "file")} {(other.Count == 1 ? "is" : "are")} something else ({Examples(other)}).");

        var nested = images.Where(p => p[(ImagesFolder.Length + 1)..].Split('/').SkipLast(1)
            .Any(s => s.Equals("ModImages", StringComparison.OrdinalIgnoreCase) || s.Equals("ModImagesDump", StringComparison.OrdinalIgnoreCase))).ToList();
        if (nested.Count > 0)
            warnings.Add($"ModImages contains another ModImages folder ({Examples(nested)}). Was the archive packed one folder too high?");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool StartsWithFolder(string path, string folder) =>
        path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    private static bool IsDll(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsImage(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static bool IsDocument(string fileName) =>
        DocumentExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
        || DocumentNames.Any(n => Path.GetFileNameWithoutExtension(fileName).StartsWith(n, StringComparison.OrdinalIgnoreCase));

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Examples(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var shown = string.Join(", ", list.Take(MaxExamples));
        return list.Count > MaxExamples ? $"{shown} and {list.Count - MaxExamples} more" : shown;
    }

    private static string Folders(ModTemplate template) => template.Folders.Count switch
    {
        1 => template.Folders[0],
        _ => string.Join(", ", template.Folders.SkipLast(1)) + " and " + template.Folders[^1],
    };
}
