using System.Text.Json;
using F3M.Desktop.Core;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.Services;

/// <summary>What an archive the user picked would install, before it is added. The import window edits it.</summary>
/// <param name="SourcePath">The archive or .dll on this computer.</param>
/// <param name="TemplateId">The mod type (<see cref="ModTemplates"/>).</param>
/// <param name="InstallPath">Game-relative, as on the upload form: the folder an archive is extracted into, or a .dll's path.</param>
/// <param name="ArchiveRoot">Archives only: install just this folder of the archive, without the folder itself.</param>
/// <param name="Entries">The file paths inside an archive; null for a plain file.</param>
public sealed record LocalModDraft(
    string SourcePath,
    string Name,
    string TemplateId,
    string InstallPath,
    string ArchiveRoot,
    IReadOnlyList<string>? Entries)
{
    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>The game-relative paths it would install. Null when the install path or archive folder is not valid.</summary>
    public List<string>? Targets() => ModTemplates.TargetsOf(FileName, Entries, InstallPath, ArchiveRoot);
}

/// <summary>
/// Mods added from an archive on this computer instead of the catalog. Each gets a version document like a catalog mod,
/// with a negative group ID so it can never be taken for one (share links and f3m:// links only accept positive IDs),
/// and its files go into the usual cache. Profiles, Deploy, conflicts, rollback and removal then treat it like any
/// other mod. The documents are not signed: the user chose the archive. The archive is read with
/// <see cref="ModFileInspector"/>, the same rules the server applies to uploads.
/// </summary>
public sealed class LocalMods(AppServices app)
{
    /// <summary>Version documents of the catalog use schema 1; a local document has the same shape.</summary>
    private const int Schema = 1;

    /// <summary>Single file, so the next ID never reuses one a deploy record of another game folder may still hold.</summary>
    private const string LastIdFile = "last-id.txt";

    public static readonly IReadOnlyList<string> Extensions = [".zip", ".7z", ".rar", ".dll"];

    private readonly Lock _gate = new();

    public static bool IsLocal(int groupId) => groupId < 0;

    /// <summary>Whether a file can be added by its extension: an archive the cache can read, or a plugin .dll.</summary>
    public static bool CanAdd(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads what is inside the file and suggests where it goes, the way the upload form does. Nothing is written.
    /// </summary>
    public LocalModDraft Analyze(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!CanAdd(path))
            throw new UserException($"{fileName} can't be added. Choose a .zip, .7z, .rar or .dll file.");
        if (!File.Exists(path))
            throw new UserException($"{fileName} no longer exists.");

        List<string>? entries = null;
        if (InstallPaths.IsArchive(fileName))
        {
            try
            {
                using var stream = File.OpenRead(path);
                entries = ModFileInspector.ListEntries(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UserException($"{fileName} could not be read: {ex.Message}");
            }

            if (entries is null)
                throw new UserException($"{fileName} could not be read. It may be damaged, encrypted, or in an unsupported format.");
            if (entries.Count == 0)
                throw new UserException($"{fileName} contains no files.");
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var suggestion = ModTemplates.Suggest(fileName, entries, name);
        return new LocalModDraft(path, name, suggestion.TemplateId, suggestion.InstallPath, suggestion.ArchiveRoot, entries);
    }

    /// <summary>
    /// The mod a type needs to work (<see cref="ModTemplate.RequiredModName"/>) and the catalog mod of that name, found
    /// the way the upload form finds it. The group is null when the catalog has no mod of that name; null when the type
    /// needs nothing.
    /// </summary>
    public (string Name, IndexGroup? Group)? RequiredMod(string templateId)
    {
        if (ModTemplates.Find(templateId)?.RequiredModName is not { } name) return null;
        var group = app.Catalog.Index?.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        return (name, group);
    }

    /// <summary>
    /// Adds the draft as a local mod: inspects the file, caches the placed files (each checked against its hash) and
    /// saves the document. The mod is not added to a profile here. Runs inside an operation when <paramref name="op"/>
    /// is given.
    /// </summary>
    public async Task<VersionDocument> AddAsync(LocalModDraft draft, OperationItem? op, CancellationToken ct)
    {
        var name = draft.Name.Trim();
        if (name.Length == 0) throw new UserException("Give the mod a name.");
        var template = ModTemplates.Find(draft.TemplateId) ?? throw new UserException("Choose the mod type.");

        op?.SetDetail($"Reading {draft.FileName}");
        InspectionResult inspection;
        try
        {
            inspection = await Task.Run(() => ModFileInspector.Inspect(draft.SourcePath, draft.FileName, draft.InstallPath, draft.ArchiveRoot), ct);
        }
        catch (InvalidDataException ex)
        {
            throw new UserException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserException($"{draft.FileName} could not be read: {ex.Message}");
        }

        var dependencies = RequiredMod(template.Id) is { Group: { } required }
            ? new List<DependencyRef> { new(required.GroupId, "latest") }
            : [];
        var file = new VersionFile(
            FileId: 1,
            draft.FileName,
            inspection.IsArchive,
            inspection.TargetPath,
            inspection.Sha256,
            inspection.Size,
            [.. inspection.Entries.Select(e => new PlacementRef(e.From, e.To, e.Sha256, e.Size, e.Kind.ToString().ToLowerInvariant()))]);

        var id = NextId();
        var added = DateTime.UtcNow;
        var doc = new VersionDocument(
            Schema,
            GroupId: id,
            VersionId: id,
            Version: added.ToLocalTime().ToString("yyyy.MM.dd"),
            name,
            Author: "Added on this computer",
            Description: $"Added from {draft.FileName}.",
            template.Category,
            added,
            PreviewUrl: null,
            [file],
            dependencies,
            GeneratedPaths: [],
            OptionalGroups: null);

        PlacementRules.ValidateVersion(doc);
        await app.Downloads.CacheLocalAsync(doc, draft.SourcePath, op, ct);
        FileOps.WriteJsonAtomic(DocumentPath(id), doc);
        AppLog.Info($"Added local mod {id} ({name}) from {draft.FileName}: {file.Placements.Count} files");
        return doc;
    }

    public VersionDocument? Get(int groupId)
    {
        if (!IsLocal(groupId)) return null;
        try
        {
            return FileOps.ReadJson<VersionDocument>(DocumentPath(groupId));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Error($"Local mod {groupId} is unreadable", ex);
            return null;
        }
    }

    public List<VersionDocument> List()
    {
        var result = new List<VersionDocument>();
        if (!Directory.Exists(app.Paths.LocalMods)) return result;
        foreach (var file in Directory.EnumerateFiles(app.Paths.LocalMods, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && Get(id) is { } doc)
                result.Add(doc);
        }

        return result;
    }

    /// <summary>
    /// Deletes every local mod that no profile of any game folder uses any more: its document and its cached files.
    /// Called after a mod leaves a profile and after a profile is deleted. A mod that is still deployed can go too:
    /// Deploy takes it out of the game folder from the deploy record, not from the cache. Returns the names deleted.
    /// </summary>
    public List<string> DeleteUnused()
    {
        var used = UsedIds();
        var deleted = new List<string>();
        foreach (var doc in List().Where(d => !used.Contains(d.GroupId)))
        {
            File.Delete(DocumentPath(doc.GroupId));
            var cached = Path.GetDirectoryName(app.Downloads.CacheDir(doc.GroupId, doc.VersionId))!;
            try
            {
                if (Directory.Exists(cached)) Directory.Delete(cached, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The document is gone, so nothing uses these files; Clear unused cache removes them later.
                AppLog.Error($"Could not delete the cached files of local mod {doc.GroupId}", ex);
            }

            AppLog.Info($"Deleted local mod {doc.GroupId} ({doc.Name}): no profile uses it");
            deleted.Add(doc.Name);
        }

        return deleted;
    }

    private string DocumentPath(int groupId) => Path.Combine(app.Paths.LocalMods, $"{groupId}.json");

    /// <summary>The next unused negative ID. IDs are never reused, even after a delete.</summary>
    private int NextId()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(app.Paths.LocalMods);
            var file = Path.Combine(app.Paths.LocalMods, LastIdFile);
            var last = File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var stored) ? Math.Min(stored, 0) : 0;
            var lowest = List().Select(d => d.GroupId).DefaultIfEmpty(0).Min();
            var next = Math.Min(last, lowest) - 1;
            FileOps.WriteTextAtomic(file, next.ToString());
            return next;
        }
    }

    /// <summary>
    /// The local mods some profile of any game folder lists. When a profile can't be read, every local mod counts as
    /// used, so nothing is deleted that it may still need.
    /// </summary>
    private HashSet<int> UsedIds()
    {
        var used = new HashSet<int>();
        if (!Directory.Exists(app.Paths.Profiles)) return used;
        foreach (var file in Directory.EnumerateFiles(app.Paths.Profiles, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (FileOps.ReadJson<ProfileDef>(file) is { } profile) used.UnionWith(profile.GroupIds.Where(IsLocal));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                AppLog.Error($"Could not read the profile {file}; local mods are kept", ex);
                return [.. List().Select(d => d.GroupId)];
            }
        }

        return used;
    }
}
