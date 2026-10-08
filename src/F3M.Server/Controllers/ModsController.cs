using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated ModsApiControllerBase — no route attributes for
/// any of the actions below, all of that comes from the generated base (see
/// obj/**/generated/RouteGen.Generators/.../Server_IModsApi.g.cs after build), itself derived
/// from the attributes on IModsApi. ModsService does the actual EF work.
///
/// Upload is the one exception: it's hand-written, not part of IModsApi/the generated base, since
/// it binds via [FromForm]/IFormFileCollection (multipart/form-data) — RouteGen's [Body] is
/// JSON-only. A controller can freely mix generated-base overrides with its own additional
/// actions like this; it doesn't have to be all-or-nothing.
/// </summary>
public sealed class ModsController(AppDbContext db, IModsApi modsApi, ILogger<ModsController> logger, CatalogService catalog) : ModsApiControllerBase
{
    public override async Task<ActionResult<ModListResult>> GetMods(
        int page, int pageSize, string? search, string? category, SortBy sort, CancellationToken ct)
        => Ok(await modsApi.GetMods(page, pageSize, search, category, sort, ct));

    public override async Task<ActionResult<Mod>> GetMod(int id, CancellationToken ct)
    {
        try
        {
            return Ok(await modsApi.GetMod(id, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    public override async Task<ActionResult<List<Mod>>> SearchMods(string query, CancellationToken ct)
        => Ok(await modsApi.SearchMods(query, ct));

    public override async Task<ActionResult<ModVersionsResult>> GetVersions(int groupId, CancellationToken ct)
    {
        try
        {
            return Ok(await modsApi.GetVersions(groupId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    public override async Task<ActionResult<List<string>>> GetCategories(CancellationToken ct) => Ok(await modsApi.GetCategories(ct));

    public override async Task<ActionResult> DownloadFile([FromRoute] int id, [FromRoute] int fileId, CancellationToken ct = default)
    {
        Stream stream;
        try
        {
            stream = await modsApi.DownloadFile(id, fileId, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }

        try
        {
            // ModsService only returns the raw stream (it has already validated existence and counted
            // the download), so look the file up again for the one thing it doesn't give us: the
            // original file name. Cheap, indexed PK lookup.
            var fileName = await db.ModFiles.Where(f => f.Id == fileId).Select(f => f.OriginalName).FirstOrDefaultAsync(ct)
                            ?? "download";

            // Hand the open FileStream to the framework instead of copying it into memory first
            // (that used to cost two full copies of the file per download, up to 1 GB for a 512 MB mod).
            // FileStreamResult streams it to the client, sets Content-Length from the seekable stream,
            // answers Range requests, and disposes the stream once the response is done.
            return File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
        }
        catch
        {
            // Ownership only passes to the result once File(...) has returned.
            await stream.DisposeAsync();
            throw;
        }
    }

    public override async Task<ActionResult<Mod>> Edit(int id, ModEditDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await modsApi.Edit(id, dto, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public override async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await modsApi.Delete(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── Upload: new mod OR new version ────────────────────────────────────────
    // Form fields:
    //   Name, Description, Version, Category, ModGroupId (optional)
    //   GeneratedPaths[i].Pattern and GeneratedPaths[i].Kind (optional, plan 5.8)
    //   previewImage (optional IFormFile)
    //   files[]           — multiple mod files
    //   installPaths[]    — one install path per file (same index)
    //   originalNames[]   — original filenames (same index)
    //
    // Everything that can reject an upload runs before any database row is written: files are staged on disk,
    // inspected, and checked against each other and against the generated-file patterns. Non-blocking warnings are
    // returned in the X-F3M-Warnings header (URL-encoded, one per line).
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(Configuration.MaxTotalSize)]
    public async Task<ActionResult<Mod>> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage,
        CancellationToken ct)
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        var userId = Helper.GetUserId(User);
        logger.LogInformation("User ID: {userId}", userId);

        if (userId is null)
            return BadRequest("User ID not found.");

        // ── Validate files ────────────────────────────────────────────────────
        if (files.Count == 0)
            return BadRequest("At least one mod file is required.");

        foreach (var f in files)
        {
            if (f.Length == 0)
                return BadRequest($"File '{f.FileName}' is empty.");

            if (f.Length > Configuration.MaxModSize)
                return BadRequest($"File '{f.FileName}' exceeds 512 MB.");

            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            if (!Configuration.AllowedFileExtension.Contains(ext))
                return BadRequest($"File type '{ext}' not allowed. Accepted: {string.Join(", ", Configuration.AllowedFileExtension)}");
        }

        // Install paths and names are author input that ends up on other people's machines.
        var cleanInstallPaths = new List<string>();
        var cleanNames = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var rawPath = i < installPaths.Count ? installPaths[i] : null;
            if (!Helper.TryNormalizeInstallPath(rawPath, out var normalizedPath))
                return BadRequest($"The install path '{rawPath}' for '{files[i].FileName}' is not valid. Use a path relative to the game folder, e.g. BepInEx/plugins/MyMod.");

            cleanInstallPaths.Add(normalizedPath);
            cleanNames.Add(Helper.SafeOriginalName(i < originalNames.Count ? originalNames[i] : null, files[i].FileName));
        }

        // Reject a bad preview image now, not after the mod group has already been created.
        var imgExt = string.Empty;
        if (previewImage is { Length: > 0 })
        {
            imgExt = Path.GetExtension(previewImage.FileName).ToLowerInvariant();
            if (!Configuration.AllowedThumbnailExtension.Contains(imgExt))
                return BadRequest($"Image type '{imgExt}' not allowed.");
            if (previewImage.Length > Configuration.MaxImageSize)
                return BadRequest("Preview image exceeds 8 MB.");
        }

        // ── Validate generated-file patterns (syntax here; collisions need the file contents) ──
        if (dto.GeneratedPaths.Count > GeneratedPathRules.MaxPatternsPerVersion)
            return BadRequest($"A version can declare at most {GeneratedPathRules.MaxPatternsPerVersion} generated-file patterns.");

        var generated = new List<GeneratedPathDto>();
        foreach (var entry in dto.GeneratedPaths)
        {
            if (!GeneratedPathRules.TryNormalize(entry.Pattern, out var pattern, out var patternError))
                return BadRequest(patternError);

            if (generated.Any(g => string.Equals(g.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
                return BadRequest($"The pattern '{pattern}' is listed more than once.");

            generated.Add(new GeneratedPathDto { Pattern = pattern, Kind = entry.Kind });
        }

        // ── Check the target group. It is created later, once the upload is known to be valid. ──
        ModGroup? existingGroup = null;
        if (dto.ModGroupId is { } modGroupId) // This is a new version of an existing mod
        {
            // Ownership belongs to the group (that is what Edit/Delete check too).
            existingGroup = await db.ModGroups.FindAsync(modGroupId);
            if (existingGroup is null)
                return BadRequest($"Mod with group ID {modGroupId} not found.");
            if (existingGroup.OwnerId != userId)
                return Forbid();
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
                var f = files[i];
                var safeName = $"{Guid.NewGuid():N}{Path.GetExtension(f.FileName).ToLowerInvariant()}";
                var diskPath = Path.Combine(Assets.Files, safeName);
                stagedPaths.Add(diskPath);

                await using (var stream = System.IO.File.Create(diskPath))
                {
                    await f.CopyToAsync(stream, ct);
                }

                staged.Add(new StagedFile(safeName, diskPath, cleanNames[i], cleanInstallPaths[i], f.Length));
            }

            foreach (var file in staged)
                inspections.Add(ModFileInspector.Inspect(file.DiskPath, file.OriginalName, file.InstallPath));

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
        }
        catch (InvalidDataException ex)
        {
            DeleteFiles(stagedPaths);
            return BadRequest(ex.Message);
        }
        catch
        {
            DeleteFiles(stagedPaths);
            throw;
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
                group = new ModGroup { Author = username, OwnerId = userId.Value };
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
                await using var imgStream = System.IO.File.Create(newPreviewPath);
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
                Author = username,
                Version = dto.Version,
                Category = dto.Category,
                PreviewImageName = previewName,
                UploadedAt = DateTime.UtcNow,
                IsApproved = true,
                UserId = userId,
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

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // A version without its files (or files without a version) must not stay behind.
            DeleteFiles(stagedPaths);
            if (newPreviewPath is not null)
                DeleteFiles(new[] { newPreviewPath });

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

        if (warnings.Count > 0)
            Response.Headers["X-F3M-Warnings"] = Uri.EscapeDataString(string.Join('\n', warnings));

        // Return with files populated
        var uploadedMod = await db.Mods
            .Include(m => m.Files)
            .Include(m => m.DependencyGroups)
            .Include(m => m.GeneratedPaths)
            .FirstAsync(m => m.Id == uploaded.Id, ct);
        return CreatedAtAction(nameof(GetMod), new { id = uploaded.Id }, uploadedMod);
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
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch
            {
                // Best effort: an orphaned file is harmless, but it must not mask the original error.
            }
        }
    }

    private sealed record StagedFile(string SafeName, string DiskPath, string OriginalName, string InstallPath, long Size);
}
