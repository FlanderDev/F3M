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
public sealed class ModsController(AppDbContext db, IModsApi modsApi, ILogger<ModsController> logger) : ModsApiControllerBase
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
    //   previewImage (optional IFormFile)
    //   files[]           — multiple mod files
    //   installPaths[]    — one install path per file (same index)
    //   originalNames[]   — original filenames (same index)
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(Configuration.MaxTotalSize)]
    public async Task<ActionResult<Mod>> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage)
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

        // ── Check if user is in mod group ─────────────────────────────────────
        ModGroup? group = null;
        var createdGroup = false;
        if (dto.ModGroupId is { } modGroupId) // This is a new version of an existing mod
        {
            // Ownership belongs to the group (that is what Edit/Delete check too); the uploader of
            // whichever version happened to be found first used to decide this.
            group = await db.ModGroups.FindAsync(modGroupId);
            if (group is null)
                return BadRequest($"Mod with group ID {modGroupId} not found.");
            if (group.OwnerId != userId)
                return Forbid();
        }

        if (group is null) // ModGroup doesn't exist yet
        {
            group = new ModGroup { Author = username, OwnerId = userId.Value };
            db.ModGroups.Add(group);
            await db.SaveChangesAsync(); // need Id before creating Mod
            createdGroup = true;
        }

        // ── Save preview image ────────────────────────────────────────────────
        string? previewName = null;
        string? newPreviewPath = null;
        if (previewImage is { Length: > 0 })
        {
            previewName = $"{Guid.NewGuid():N}{imgExt}";
            newPreviewPath = Path.Combine(Assets.Images, previewName);
            await using var imgStream = System.IO.File.Create(newPreviewPath);
            await previewImage.CopyToAsync(imgStream);
        }
        else if (dto.ModGroupId.HasValue)
        {
            // Inherit preview from the previous latest version if none supplied
            var prev = await db.Mods
                .Where(m => m.ModGroupId == dto.ModGroupId.Value)
                .OrderByDescending(m => m.UploadedAt)
                .FirstOrDefaultAsync();
            previewName = prev?.PreviewImageName;
        }

        // ── Resolve dependencies ──────────────────────────────────────────────
        // A mod can't depend on its own group (including a brand new one it just created above).
        var dependencyGroups = new List<ModGroup>();
        if (dto.DependencyGroupIds.Count > 0)
        {
            var requestedIds = dto.DependencyGroupIds.Where(depId => depId != group.Id).Distinct().ToList();
            dependencyGroups = await db.ModGroups.Where(g => requestedIds.Contains(g.Id)).ToListAsync();
        }

        // ── Create version record ─────────────────────────────────────────────
        var mod = new Mod
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
        await db.SaveChangesAsync(); // need mod.Id for ModFile FKs

        // ── Save each mod file ────────────────────────────────────────────────
        var written = new List<string>();
        try
        {
            for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            var safeName = $"{Guid.NewGuid():N}{ext}";
                var diskPath = Path.Combine(Assets.Files, safeName);

                await using (var stream = System.IO.File.Create(diskPath))
                {
                    written.Add(diskPath);
            await f.CopyToAsync(stream);
                }

            db.ModFiles.Add(new ModFile
            {
                ModId = mod.Id,
                FileName = safeName,
                    OriginalName = cleanNames[i],
                    InstallPath = cleanInstallPaths[i],
                FileSizeBytes = f.Length
            });
        }

        await db.SaveChangesAsync();
        }
        catch
        {
            // A version without its files (or files without a version) must not stay behind.
            foreach (var orphan in written)
            {
                try { System.IO.File.Delete(orphan); } catch { /* ignore */ }
            }

            if (newPreviewPath is not null)
            {
                try { System.IO.File.Delete(newPreviewPath); } catch { /* ignore */ }
            }

            try
            {
                db.ChangeTracker.Clear();
                await db.Mods.Where(m => m.Id == mod.Id).ExecuteDeleteAsync();
                if (createdGroup)
                    await db.ModGroups.Where(g => g.Id == group.Id).ExecuteDeleteAsync();
                else
                {
                    await ModsService.RecalculateLatestVersion(db, group.Id);
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception cleanupError)
            {
                logger.LogError(cleanupError, "Cleaning up the failed upload of {Name} failed as well.", mod.Name);
            }

            throw;
        }

        logger.LogInformation("Mod uploaded: {Name} v{Version} by {Author} ({FileCount} files)", mod.Name, mod.Version, mod.Author, files.Count);

        // Return with files populated
        var uploadedMod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups).FirstAsync(m => m.Id == mod.Id);
        return CreatedAtAction(nameof(GetMod), new { id = mod.Id }, uploadedMod);
    }
}
