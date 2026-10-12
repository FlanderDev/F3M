using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>Who a new mod or version is stored for.</summary>
/// <param name="UserId">Owner of a new group; must also own an existing group the upload adds a version to.</param>
/// <param name="Name">Shown as the author of the version and of a new group.</param>
public sealed record UploadOwner(int UserId, string Name);

/// <summary>
/// For mods imported from elsewhere: who posted them there (F95zone user id and name) and where. Stored on a new
/// group, so the mod can go to that F95 user when they sign in.
/// </summary>
public sealed record UploadOrigin(string? F95UserId, string? F95UserName, string? SourceUrl);

/// <summary>
/// One mod file of an upload: its content, its original name, where it goes in the game folder, for archives
/// optionally the folder inside the archive to install (see <see cref="ModFile.ArchiveRoot"/>), and what kind of mod
/// file it is (a <see cref="ModTemplates"/> id; none means <see cref="ModTemplates.Custom"/>).
/// </summary>
public sealed record UploadFile(IFormFile Content, string? OriginalName, string? InstallPath, string? ArchiveRoot = null, string? Template = null)
{
    /// <summary>The files of an upload form, from its lists that run parallel to the files (shorter lists leave the rest empty).</summary>
    public static List<UploadFile> FromForm(
        IFormFileCollection files,
        IReadOnlyList<string>? originalNames,
        IReadOnlyList<string>? installPaths,
        IReadOnlyList<string>? archiveRoots,
        IReadOnlyList<string>? templates)
    {
        static string? At(IReadOnlyList<string>? list, int i) => list is not null && i < list.Count ? list[i] : null;
        return [.. files.Select((f, i) => new UploadFile(f, At(originalNames, i), At(installPaths, i), At(archiveRoots, i), At(templates, i)))];
    }

    /// <summary>The <see cref="Template"/> that asks the server to recognise the file (imports only).</summary>
    public const string AutoTemplate = "auto";

    /// <summary>
    /// For <see cref="AutoTemplate"/>: the file with what its contents show. Without an install path, the install path
    /// and the archive folder (inside the given one, if any) come from <see cref="ModTemplates.Suggest"/>; the type is
    /// then the one its installed paths fit (<see cref="ModTemplates.Classify"/>). Other files are returned as they are.
    /// </summary>
    public UploadFile Recognise(string? modName)
    {
        if (!string.Equals(Template, AutoTemplate, StringComparison.OrdinalIgnoreCase))
            return this;

        var name = Helper.SafeOriginalName(OriginalName, Content.FileName);
        List<string>? entries = null;
        if (InstallPaths.IsArchive(name))
        {
            using var stream = Content.OpenReadStream();
            entries = ModFileInspector.ListEntries(stream);
            if (entries is null) // unreadable: the upload reports it
                return this with { Template = ModTemplates.Custom };
        }

        var installPath = InstallPath ?? string.Empty;
        var root = ArchiveRoot ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            if (!InstallPaths.TrySplit(root, out var rootParts, out _))
                return this with { Template = ModTemplates.Custom };

            var inside = entries?
                .Select(e => InstallPaths.TrySplit(e, out var parts, out _) && InstallPaths.TryStripRoot(rootParts, parts, out var relative)
                    ? string.Join('/', relative)
                    : rootParts.Count == 0 ? e : null)
                .OfType<string>()
                .ToList();
            var suggestion = ModTemplates.Suggest(name, inside, modName);
            installPath = suggestion.InstallPath;
            root = string.Join('/', new[] { string.Join('/', rootParts), suggestion.ArchiveRoot }.Where(p => p.Length > 0));
        }

        var targets = ModTemplates.TargetsOf(name, entries, installPath, root) ?? [];
        return this with { InstallPath = installPath, ArchiveRoot = root, Template = ModTemplates.Classify(targets) };
    }

    /// <summary>The game paths the file installs with its install path and archive folder; empty when unreadable or invalid.</summary>
    public List<string> Targets()
    {
        var name = Helper.SafeOriginalName(OriginalName, Content.FileName);
        List<string>? entries = null;
        if (InstallPaths.IsArchive(name))
        {
            using var stream = Content.OpenReadStream();
            entries = ModFileInspector.ListEntries(stream);
            if (entries is null)
                return [];
        }

        return ModTemplates.TargetsOf(name, entries, InstallPath, ArchiveRoot) ?? [];
    }
}

/// <summary>
/// The outcome of <see cref="ModUploadService.UploadAsync"/>. <see cref="Error"/> is safe to show the uploader;
/// <see cref="Forbidden"/> means the target group belongs to someone else. <see cref="Unconfirmed"/> lists the
/// warnings about the files' types when the uploader has not accepted them yet; nothing was stored then.
/// </summary>
public sealed record UploadResult(Mod? Mod, string? Error, bool Forbidden, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Unconfirmed = null)
{
    public static UploadResult Fail(string error) => new(null, error, false, []);

    public static UploadResult NeedsConfirmation(IReadOnlyList<string> warnings) => new(null, null, false, [], warnings);
}

/// <summary>
/// Stores a new mod or a new version of one. Used by the upload form (ModsController.Upload) and, in debug builds,
/// by the bulk importer (DebugController), so both go through the same checks and produce the same catalog data.
///
/// Everything that can reject an upload runs before any database row is written: files are staged on disk,
/// inspected, and checked against each other and against the generated-file patterns. Files are also checked against
/// their types (<see cref="ModTemplates"/>); those findings only hold the upload back until the uploader accepts them.
/// </summary>
public sealed class ModUploadService(AppDbContext db, CatalogService catalog, ILogger<ModUploadService> logger)
{
    /// <param name="uploadedAt">Upload time to record; defaults to now. The importer passes the original post date.</param>
    /// <param name="origin">Imports only: where the mod was published before; used when the upload creates the group.</param>
    public async Task<UploadResult> UploadAsync(
        ModUploadDto dto,
        IReadOnlyList<UploadFile> files,
        IFormFile? previewImage,
        UploadOwner owner,
        DateTime? uploadedAt = null,
        UploadOrigin? origin = null,
        CancellationToken ct = default)
    {
        // ── Validate files ────────────────────────────────────────────────────
        if (files.Count == 0)
            return UploadResult.Fail("At least one mod file is required.");

        foreach (var f in files.Select(x => x.Content))
        {
            if (f.Length == 0)
                return UploadResult.Fail($"File '{f.FileName}' is empty.");

            if (f.Length > Configuration.MaxModSize)
                return UploadResult.Fail($"File '{f.FileName}' exceeds 512 MB.");

            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            if (!Configuration.AllowedFileExtension.Contains(ext))
                return UploadResult.Fail($"File type '{ext}' not allowed. Accepted: {string.Join(", ", Configuration.AllowedFileExtension)}");
        }

        // Install paths and names are author input that ends up on other people's machines.
        var cleanInstallPaths = new List<string>();
        var cleanNames = new List<string>();
        var cleanRoots = new List<string>();
        var cleanTemplates = new List<string>();
        foreach (var file in files)
        {
            if (!Helper.TryNormalizeInstallPath(file.InstallPath, out var normalizedPath))
                return UploadResult.Fail($"The install path '{file.InstallPath}' for '{file.Content.FileName}' is not valid. Use a path relative to the game folder, e.g. BepInEx/plugins/MyMod.");

            if (!InstallPaths.TryNormalize(file.ArchiveRoot, out var normalizedRoot))
                return UploadResult.Fail($"The archive folder '{file.ArchiveRoot}' for '{file.Content.FileName}' is not valid.");

            if (!ModTemplates.TryNormalizeId(file.Template, out var template))
                return UploadResult.Fail($"'{file.Template}' for '{file.Content.FileName}' is not a known mod type.");

            cleanInstallPaths.Add(normalizedPath);
            cleanNames.Add(Helper.SafeOriginalName(file.OriginalName, file.Content.FileName));
            cleanRoots.Add(normalizedRoot);
            cleanTemplates.Add(template);
        }

        // Reject a bad preview image now, not after the mod group has already been created.
        var imgExt = string.Empty;
        if (previewImage is { Length: > 0 })
        {
            imgExt = Path.GetExtension(previewImage.FileName).ToLowerInvariant();
            if (!Configuration.AllowedThumbnailExtension.Contains(imgExt))
                return UploadResult.Fail($"Image type '{imgExt}' not allowed.");
            if (previewImage.Length > Configuration.MaxImageSize)
                return UploadResult.Fail("Preview image exceeds 8 MB.");
        }

        // ── Validate generated-file patterns (syntax here; collisions need the file contents) ──
        if (dto.GeneratedPaths.Count > GeneratedPathRules.MaxPatternsPerVersion)
            return UploadResult.Fail($"A version can declare at most {GeneratedPathRules.MaxPatternsPerVersion} generated-file patterns.");

        var generated = new List<GeneratedPathDto>();
        foreach (var entry in dto.GeneratedPaths)
        {
            if (!GeneratedPathRules.TryNormalize(entry.Pattern, out var pattern, out var patternError))
                return UploadResult.Fail(patternError ?? $"The pattern '{entry.Pattern}' is not valid.");

            if (generated.Any(g => string.Equals(g.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
                return UploadResult.Fail($"The pattern '{pattern}' is listed more than once.");

            generated.Add(new GeneratedPathDto { Pattern = pattern, Kind = entry.Kind });
        }

        // ── Validate optional groups (what they cover needs the file contents) ──
        if (!OptionalFileRules.TryNormalize(dto.OptionalGroups, out var optionalGroups, out var optionalError))
            return UploadResult.Fail(optionalError ?? "The optional groups are not valid.");

        // ── Check the target group. It is created later, once the upload is known to be valid. ──
        ModGroup? existingGroup = null;
        if (dto.ModGroupId is { } modGroupId) // This is a new version of an existing mod
        {
            // Ownership belongs to the group (that is what Edit/Delete check too).
            existingGroup = await db.ModGroups.FindAsync([modGroupId], ct);
            if (existingGroup is null)
                return UploadResult.Fail($"Mod with group ID {modGroupId} not found.");
            if (existingGroup.OwnerId != owner.UserId)
                return new UploadResult(null, null, true, []);
        }

        // ── Stage the files on disk, inspect them, and check them against each other ──
        var stagedPaths = new List<string>(); // every file written, so a failure can remove them
        var staged = new List<StagedFile>();
        var inspections = new List<InspectionResult>();
        var warnings = new List<string>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i].Content;
                var safeName = $"{Guid.NewGuid():N}{Path.GetExtension(f.FileName).ToLowerInvariant()}";
                var diskPath = Path.Combine(Assets.Files, safeName);
                stagedPaths.Add(diskPath);

                await using (var stream = File.Create(diskPath))
                {
                    await f.CopyToAsync(stream, ct);
                }

                staged.Add(new StagedFile(safeName, diskPath, cleanNames[i], cleanInstallPaths[i], cleanRoots[i], cleanTemplates[i], f.Length));
            }

            foreach (var file in staged)
                inspections.Add(ModFileInspector.Inspect(file.DiskPath, file.OriginalName, file.InstallPath, file.ArchiveRoot));

            // Two files, or two archive entries, must not install over each other.
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in inspections.SelectMany(x => x.Entries))
            {
                if (!targets.Add(entry.To))
                    throw new InvalidDataException($"More than one file would be installed to '{entry.To}'.");
            }

            // A generated pattern must never cover a file the mod ships itself.
            foreach (var pattern in generated)
            {
                var hit = targets.FirstOrDefault(path => GeneratedPathRules.IsMatch(pattern.Pattern, path));
                if (hit is not null)
                    throw new InvalidDataException($"The pattern '{pattern.Pattern}' would match '{hit}', which the mod installs itself. Narrow the pattern.");
            }

            warnings.AddRange(await FindOverlapWarningsAsync(generated, dto.ModGroupId, ct));

            // Optional groups must leave something to install; groups or patterns that match nothing are only noted.
            var optionalReport = OptionalFileRules.Analyse(optionalGroups, targets);
            if (optionalReport.Errors.Count > 0)
                throw new InvalidDataException(optionalReport.Errors[0]);
            warnings.AddRange(optionalReport.Warnings);

            foreach (var pattern in generated.Where(g => GeneratedPathRules.HasDeepWildcard(g.Pattern)))
                warnings.Add($"'{pattern.Pattern}' matches at any depth. Check that it cannot reach files other mods create.");

            // Files that do not look like their type are stored only once the uploader has accepted that. Optional
            // files are not installed by default, so only the rest is checked.
            var typeWarnings = new List<string>();
            for (var i = 0; i < staged.Count; i++)
            {
                var fileTargets = OptionalFileRules.Installed(optionalGroups, inspections[i].Entries.Select(e => e.To));
                typeWarnings.AddRange(ModTemplates.Check(staged[i].Template, fileTargets).Select(w => $"{staged[i].OriginalName}: {w}"));
            }

            typeWarnings.AddRange(await FindMissingRequirementsAsync(staged.Select(f => f.Template), dto, ct));
            if (typeWarnings.Count > 0 && !dto.AcceptWarnings)
            {
                DeleteFiles(stagedPaths);
                return UploadResult.NeedsConfirmation(typeWarnings);
            }

            if (typeWarnings.Count > 0)
                logger.LogInformation("Upload of {Name} accepted with {Count} type warnings: {Warnings}", dto.Name, typeWarnings.Count, typeWarnings);
        }
        catch (InvalidDataException ex)
        {
            DeleteFiles(stagedPaths);
            return UploadResult.Fail(ex.Message);
        }
        catch
        {
            DeleteFiles(stagedPaths);
            throw;
        }

        // Without a category of its own, a mod gets the one of its first typed file.
        var category = dto.Category;
        if (string.IsNullOrWhiteSpace(category) || category == ModUploadDto.UnspecifiedCategory)
        {
            category = staged.Select(f => ModTemplates.Find(f.Template)).FirstOrDefault(t => t is { Id: not ModTemplates.Custom })?.Category
                       ?? category;
        }

        // ── Write the rows. If anything fails from here on, rows and staged files are removed again. ──
        var group = existingGroup;
        var createdGroup = false;
        string? newPreviewPath = null;
        Mod? mod = null;
        try
        {
            if (group is null) // ModGroup doesn't exist yet
            {
                group = new ModGroup
                {
                    Author = owner.Name,
                    OwnerId = owner.UserId,
                    F95OwnerUserId = origin?.F95UserId,
                    F95OwnerName = origin?.F95UserName,
                    SourceUrl = origin?.SourceUrl,
                    // An import for an F95 user who already has an account goes to them straight away.
                    ClaimedAt = origin?.F95UserId is not null && owner.UserId != ModGroup.UnclaimedOwnerId ? DateTime.UtcNow : null
                };
                db.ModGroups.Add(group);
                await db.SaveChangesAsync(ct); // need Id before creating Mod
                createdGroup = true;
            }

            // ── Save preview image ──
            string? previewName = null;
            if (previewImage is { Length: > 0 })
            {
                previewName = $"{Guid.NewGuid():N}{imgExt}";
                newPreviewPath = Path.Combine(Assets.Images, previewName);
                await using var imgStream = File.Create(newPreviewPath);
                await previewImage.CopyToAsync(imgStream, ct);
            }
            else if (dto.ModGroupId.HasValue)
            {
                // Inherit preview from the previous latest version if none supplied
                var prev = await db.Mods
                    .Where(m => m.ModGroupId == dto.ModGroupId.Value)
                    .OrderByDescending(m => m.UploadedAt)
                    .FirstOrDefaultAsync(ct);
                previewName = prev?.PreviewImageName;
            }

            // ── Resolve dependencies ──
            // A mod can't depend on its own group (including a brand new one it just created above).
            var dependencyGroups = new List<ModGroup>();
            if (dto.DependencyGroupIds.Count > 0)
            {
                var requestedIds = dto.DependencyGroupIds.Where(depId => depId != group.Id).Distinct().ToList();
                dependencyGroups = await db.ModGroups.Where(g => requestedIds.Contains(g.Id)).ToListAsync(ct);
            }

            // ── Create version record ──
            mod = new Mod
            {
                ModGroupId = group.Id,
                Name = dto.Name,
                Description = dto.Description,
                Author = owner.Name,
                Version = dto.Version,
                Category = category,
                PreviewImageName = previewName,
                UploadedAt = uploadedAt ?? DateTime.UtcNow,
                IsApproved = true,
                UserId = owner.UserId,
                DependencyGroups = dependencyGroups
            };

            await ModsService.RecalculateLatestVersion(db, group.Id, mod);
            db.Mods.Add(mod);
            await db.SaveChangesAsync(ct); // need mod.Id for ModFile FKs

            // ── Files, their placements, and the generated-file patterns ──
            for (var i = 0; i < staged.Count; i++)
            {
                var file = staged[i];
                var inspection = inspections[i];

                var modFile = new ModFile
                {
                    ModId = mod.Id,
                    FileName = file.SafeName,
                    OriginalName = file.OriginalName,
                    InstallPath = file.InstallPath,
                    ArchiveRoot = file.ArchiveRoot,
                    Template = file.Template,
                    FileSizeBytes = file.Size,
                    Sha256 = inspection.Sha256,
                    IsArchive = inspection.IsArchive,
                    TargetPath = inspection.TargetPath
                };
                db.ModFiles.Add(modFile);
                await db.SaveChangesAsync(ct); // need modFile.Id for its entries

                db.ModFileEntries.AddRange(inspection.Entries.Select(e => new ModFileEntry
                {
                    ModFileId = modFile.Id,
                    From = e.From,
                    To = e.To,
                    Size = e.Size,
                    Sha256 = e.Sha256,
                    Kind = e.Kind
                }));
            }

            db.ModGeneratedPaths.AddRange(generated.Select(g => new ModGeneratedPath
            {
                ModId = mod.Id,
                Pattern = g.Pattern,
                Kind = g.Kind
            }));

            db.ModOptionalGroups.AddRange(optionalGroups.Select(g => new ModOptionalGroup
            {
                ModId = mod.Id,
                Name = g.Name,
                Patterns = g.Patterns
            }));

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // A version without its files (or files without a version) must not stay behind.
            DeleteFiles(stagedPaths);
            if (newPreviewPath is not null)
                DeleteFiles([newPreviewPath]);

            try
            {
                db.ChangeTracker.Clear();
                if (mod is { } created)
                    await db.Mods.Where(m => m.Id == created.Id).ExecuteDeleteAsync();

                if (createdGroup && group is { } newGroup)
                {
                    await db.ModGroups.Where(g => g.Id == newGroup.Id).ExecuteDeleteAsync();
                }
                else if (group is { } existing)
                {
                    await ModsService.RecalculateLatestVersion(db, existing.Id);
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception cleanupError)
            {
                logger.LogError(cleanupError, "Cleaning up the failed upload of {Name} failed as well.", dto.Name);
            }

            throw;
        }

        var uploaded = mod!;
        logger.LogInformation("Mod uploaded: {Name} v{Version} by {Author} ({FileCount} files)", uploaded.Name, uploaded.Version, uploaded.Author, files.Count);

        // The catalog is updated after the upload is safely stored. A failure there is logged, never returned to the uploader.
        await catalog.TryPublishVersionAsync(uploaded.Id, ct);

        // Return with files populated
        var uploadedMod = await db.Mods
            .Include(m => m.Files)
            .Include(m => m.DependencyGroups)
            .Include(m => m.GeneratedPaths)
            .Include(m => m.OptionalGroups)
            .FirstAsync(m => m.Id == uploaded.Id, ct);
        return new UploadResult(uploadedMod, null, false, warnings);
    }

    /// <summary>
    /// The mods the files' types need (the Custom Missions framework, the Cosplay Loader, ...) that exist on this site
    /// but are not among the upload's requirements. A required mod that is not on the site is not mentioned, and
    /// neither is the requirement when the upload is a new version of that mod itself.
    /// </summary>
    private async Task<List<string>> FindMissingRequirementsAsync(IEnumerable<string> templateIds, ModUploadDto dto, CancellationToken ct)
    {
        var warnings = new List<string>();
        var templates = templateIds.Distinct().Select(ModTemplates.Find).OfType<ModTemplate>().Where(t => t.RequiredModName is not null);
        foreach (var template in templates)
        {
            var name = template.RequiredModName!.ToLower();
            var groups = await db.Mods.AsNoTracking()
                .Where(m => m.IsLatestVersion && m.IsApproved && m.Name.ToLower() == name)
                .Select(m => m.ModGroupId)
                .Distinct()
                .ToListAsync(ct);

            var isThatMod = dto.ModGroupId is { } self && groups.Contains(self);
            if (groups.Count == 0 || isThatMod || groups.Any(dto.DependencyGroupIds.Contains))
                continue;

            warnings.Add($"{template.Name} files need {template.RequiredModName}. Add it as a requirement so that players get it too.");
        }

        return warnings;
    }

    /// <summary>Warns about patterns that may overlap a pattern of another approved mod. Overlaps never block an upload.</summary>
    private async Task<List<string>> FindOverlapWarningsAsync(IReadOnlyList<GeneratedPathDto> patterns, int? excludeGroupId, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (patterns.Count == 0)
            return warnings;

        var query = from p in db.ModGeneratedPaths.AsNoTracking()
                    join m in db.Mods.AsNoTracking() on p.ModId equals m.Id
                    where m.IsApproved
                    select new { p.Pattern, m.Name, m.ModGroupId };

        // Versions of the same mod may overlap their own earlier versions without a warning.
        if (excludeGroupId is { } groupId)
            query = query.Where(x => x.ModGroupId != groupId);

        var existing = await query.ToListAsync(ct);

        foreach (var pattern in patterns)
        {
            foreach (var other in existing)
            {
                if (GeneratedPathRules.MayOverlap(pattern.Pattern, other.Pattern))
                    warnings.Add($"'{pattern.Pattern}' may overlap '{other.Pattern}' from {other.Name}.");
            }
        }

        return warnings;
    }

    private static void DeleteFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort: an orphaned file is harmless, but it must not mask the original error.
            }
        }
    }

    private sealed record StagedFile(string SafeName, string DiskPath, string OriginalName, string InstallPath, string ArchiveRoot, string Template, long Size);
}
