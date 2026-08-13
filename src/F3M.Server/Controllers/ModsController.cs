using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Controllers;

[ApiController]
[Route(Endpoints.Mods.Base)]
public sealed class ModsController(AppDbContext db, ILogger<ModsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ModListResult>> GetMods(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 18,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] SortBy sort = SortBy.Newest)
    {
        // Latest version per group: pick the Mod row with the highest UploadedAt per ModGroupId
        var latestIds = db.Mods
            .Where(m => m.IsApproved)
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var query = db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Include(m => m.Files)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(m => m.Name.Contains(search));

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(m => m.Category == category);

        query = sort switch
        {
            SortBy.Newest => query.OrderByDescending(m => m.UploadedAt),
            SortBy.Oldest => query.OrderBy(m => m.UploadedAt),
            SortBy.DownloadsDesc => query.OrderByDescending(m => m.DownloadCount),
            SortBy.DownloadsAsc => query.OrderBy(m => m.DownloadCount),
            SortBy.NameAsc => query.OrderBy(m => m.Name),
            SortBy.NameDesc => query.OrderByDescending(m => m.Name),
            _ => query.OrderByDescending(m => m.UploadedAt)
        };

        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Ok(new ModListResult { Items = items, TotalCount = items.Count, Page = page, PageSize = pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Mod>> GetMod(int id)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        return mod is null ? NotFound() : Ok(mod);
    }

    [HttpGet("{query}")]
    public async Task<ActionResult<Mod>> GetMod(string query)
    {
        var mods = await db.Mods.Where(w => w.Name.Contains(query)).ToArrayAsync();
        return mods is null ? NotFound() : Ok(mods);
    }

    [HttpGet($"{Endpoints.Group}/{{groupId:int}}/{Endpoints.Versions}")]
    public async Task<ActionResult<ModVersionsResult>> GetVersions(int groupId)
    {
        var group = await db.ModGroups.FindAsync(groupId);
        if (group is null) return NotFound();

        var versions = await db.Mods
            .Where(m => m.ModGroupId == groupId && m.IsApproved)
            .Include(m => m.Files)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync();

        return Ok(new ModVersionsResult { Group = group, Versions = versions });
    }

    [HttpGet(Endpoints.Categories)]
    public async Task<ActionResult<List<ValueTuple<string, int>>>> GetCategories()
    {
        // Category of each group = category of its latest version
        var latestIds = db.Mods
            .Where(m => m.IsApproved)
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var cats = await db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Select(m => m.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        return Ok(cats);
    }

    // ── Upload: new mod OR new version ────────────────────────────────────────
    // Form fields:
    //   Name, Description, Version, Category, ModGroupId (optional)
    //   previewImage (optional IFormFile)
    //   files[]           — multiple mod files
    //   installPaths[]    — one install path per file (same index)
    //   originalNames[]   — original filenames (same index)
    [HttpPost(Endpoints.Upload)]
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
        logger.LogInformation($"User ID: {userId}");

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
            UserId = userId
        };

        await RecalculateLatestVersion(db, group.Id);
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
        var uploadedMod = await db.Mods.Include(m => m.Files).FirstAsync(m => m.Id == mod.Id);
        return CreatedAtAction(nameof(GetMod), new { id = mod.Id }, uploadedMod);
    }

    [HttpPost($"{{id:int}}/{Endpoints.Download}/{{fileId:int}}")]
    public async Task<IActionResult> Download(int id, int fileId)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null)
            return NotFound();

        var file = mod.Files.FirstOrDefault(f => f.Id == fileId);
        if (file is null)
            return NotFound("File not found in this mod version.");

        mod.DownloadCount++;
        await db.SaveChangesAsync();

        var path = Path.Combine(Assets.Files, file.FileName);
        if (!System.IO.File.Exists(path))
            return NotFound("File not found on server.");

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, "application/octet-stream", file.OriginalName);
    }


    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<ActionResult<Mod>> Edit(int id, [FromBody] ModEditDto dto)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null) return NotFound();

        var userId = Helper.GetUserId(User);
        var isAdmin = User.IsInRole(AppRoles.Admin);
        var group = await db.ModGroups.FindAsync(mod.ModGroupId);
        // logger.LogInformation($"ModGroup: {group?.OwnerId} vs {userId}");
        if (!isAdmin && group?.OwnerId != null && group.OwnerId != userId) return Forbid();

        mod.Name = dto.Name;
        mod.Description = dto.Description;
        mod.Version = dto.Version;
        mod.Category = dto.Category;

        await RecalculateLatestVersion(db, group?.Id, mod);
        await db.SaveChangesAsync();
        return Ok(mod);
    }

    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null) return NotFound();

        var userId = Helper.GetUserId(User);
        var isAdmin = User.IsInRole(AppRoles.Admin);
        var group = await db.ModGroups.FindAsync(mod.ModGroupId);
        if (!isAdmin && group?.OwnerId != null && group.OwnerId != userId) return Forbid();

        // Delete uploaded files from disk
        foreach (var f in mod.Files)
        {
            var p = Path.Combine(Assets.Files, f.FileName);
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
        }

        db.Mods.Remove(mod);

        // If this was the last version in the group, remove the group too
        var remaining = await db.Mods.CountAsync(m => m.ModGroupId == mod.ModGroupId && m.Id != id);
        if (remaining == 0 && group is not null)
            db.ModGroups.Remove(group);

        await RecalculateLatestVersion(db, group?.Id, mod);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static async Task RecalculateLatestVersion(AppDbContext appDbContext, int? modGroupId, Mod? incoming = null)
    {
        var existing = await appDbContext.Mods.Where(m => m.ModGroupId == modGroupId).ToListAsync();
        if (existing.Count == 0)
        {
            incoming?.IsLatestVersion = true;
            return;
        }

        var all = incoming is not null
            ? existing.Append(incoming)
            : existing;

        var latest = all.OrderByDescending(m => new Version(m.Version)).First();
        foreach (var m in all)
            m.IsLatestVersion = m.Id == latest.Id;
    }
}
