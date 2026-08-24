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
        try
        {
            await using var stream = await modsApi.DownloadFile(id, fileId, ct);
            // ModsService already validated existence/incremented the download count — we just
            // need the original filename for the response, which it doesn't have (it only
            // returns the raw stream), so look the file up again for that one field. Cheap
            // (indexed PK lookups), and keeps IModsApi's DownloadFile signature to just Stream
            // rather than a wrapper DTO.
            var fileName = await db.ModFiles.Where(f => f.Id == fileId).Select(f => f.OriginalName).FirstOrDefaultAsync(ct)
                            ?? "download";
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return File(ms.ToArray(), "application/octet-stream", fileName);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
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

        // ── Check if user is in mod group ─────────────────────────────────────
        ModGroup? group = null;
        if (dto.ModGroupId is { } modGroupId) // This is a new version of an existing mod
        {
            var existing = db.Mods.FirstOrDefault(m => m.ModGroupId == modGroupId);
            if (existing is null)
                return BadRequest($"Mod with group ID {modGroupId} not found.");
            if (existing.UserId != userId)
                return Forbid();

            var existingModGroup = db.ModGroups.FirstOrDefault(m => m.Id == modGroupId);
            if (existingModGroup is not null)
                group = existingModGroup;
        }

        if (group is null) // ModGroup doesn't exist yet
        {
            if (userId == null)
                return Forbid();

            group = new ModGroup { Author = username, OwnerId = userId.Value };
            db.ModGroups.Add(group);
            await db.SaveChangesAsync(); // need Id before creating Mod
        }

        // ── Save preview image ────────────────────────────────────────────────
        string? previewName = null;
        if (previewImage is { Length: > 0 })
        {
            var imgExt = Path.GetExtension(previewImage.FileName).ToLowerInvariant();
            if (!Configuration.AllowedThumbnailExtension.Contains(imgExt))
                return BadRequest($"Image type '{imgExt}' not allowed.");
            if (previewImage.Length > Configuration.MaxImageSize)
                return BadRequest("Preview image exceeds 8 MB.");

            previewName = $"{Guid.NewGuid():N}{imgExt}";
            await using var imgStream = System.IO.File.Create(Path.Combine(Assets.Images, previewName));
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

        await ModsService.RecalculateLatestVersion(db, group.Id);
        db.Mods.Add(mod);
        await db.SaveChangesAsync(); // need mod.Id for ModFile FKs

        // ── Save each mod file ────────────────────────────────────────────────
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            var safeName = $"{Guid.NewGuid():N}{ext}";
            var origName = i < originalNames.Count ? originalNames[i] : f.FileName;
            var installPath = i < installPaths.Count ? (installPaths[i] ?? string.Empty).Trim() : string.Empty;

            await using var stream = System.IO.File.Create(Path.Combine(Assets.Files, safeName));
            await f.CopyToAsync(stream);

            db.ModFiles.Add(new ModFile
            {
                ModId = mod.Id,
                FileName = safeName,
                OriginalName = origName,
                InstallPath = installPath,
                FileSizeBytes = f.Length
            });
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Mod uploaded: {Name} v{Version} by {Author} ({FileCount} files)", mod.Name, mod.Version, mod.Author, files.Count);

        // Return with files populated
        var uploadedMod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups).FirstAsync(m => m.Id == mod.Id);
        return CreatedAtAction(nameof(GetMod), new { id = mod.Id }, uploadedMod);
    }
}
