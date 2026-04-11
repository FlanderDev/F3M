using System.Security.Claims;
using F3M.Server.Data;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModsController(AppDbContext db, IWebHostEnvironment env, ILogger<ModsController> logger) : ControllerBase
{
    private static readonly string[] AllowedModExtensions   = [".zip", ".rar", ".7z", ".pak", ".mod"];
    private static readonly string[] AllowedImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private const long MaxModSize    = 512 * 1024 * 1024;
    private const long MaxImageSize  = 8   * 1024 * 1024;
    private const long MaxTotalSize  = 2L  * 1024 * 1024 * 1024; // 2 GB total per upload

    // ── Browse: one row per group (latest version only) ───────────────────────
    // GET /api/mods?page=1&pageSize=18&search=&category=&sort=newest
    [HttpGet]
    public async Task<ActionResult<ModListResult>> GetMods(
        [FromQuery] int     page      = 1,
        [FromQuery] int     pageSize  = 18,
        [FromQuery] string? search    = null,
        [FromQuery] string? category  = null,
        [FromQuery] string? sort      = "newest")
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
            query = query.Where(m =>
                m.Name.Contains(search) ||
                m.Description.Contains(search) ||
                m.Author.Contains(search));

        if (!string.IsNullOrWhiteSpace(category) && category != "All")
            query = query.Where(m => m.Category == category);

        query = sort switch
        {
            "popular" => query.OrderByDescending(m => m.DownloadCount),
            "name"    => query.OrderBy(m => m.Name),
            _         => query.OrderByDescending(m => m.UploadedAt)
        };

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new ModListResult { Items = items, TotalCount = total, Page = page, PageSize = pageSize });
    }

    // GET /api/mods/{id}  — single version with files
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Mod>> GetMod(int id)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        return mod is null ? NotFound() : Ok(mod);
    }

    // GET /api/mods/group/{groupId}/versions  — all versions of a group
    [HttpGet("group/{groupId:int}/versions")]
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

    // GET /api/mods/categories
    [HttpGet("categories")]
    public async Task<ActionResult<List<string>>> GetCategories()
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
    // POST /api/mods/upload
    // Form fields:
    //   Name, Description, Version, Category, ModGroupId (optional)
    //   previewImage (optional IFormFile)
    //   files[]           — multiple mod files
    //   installPaths[]    — one install path per file (same index)
    //   originalNames[]   — original filenames (same index)
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(MaxTotalSize)]
    public async Task<ActionResult<Mod>> Upload(
        [FromForm] ModUploadDto      dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string>       installPaths,
        [FromForm] List<string>       originalNames,
        IFormFile?                    previewImage)
    {
        var username = User.FindFirstValue("name") ?? "unknown";
        var userId   = int.TryParse(User.FindFirstValue("sub"), out var uid) ? uid : (int?)null;

        // ── Validate files ────────────────────────────────────────────────────
        if (files.Count == 0)
            return BadRequest("At least one mod file is required.");

        foreach (var f in files)
        {
            if (f.Length == 0)       return BadRequest($"File '{f.FileName}' is empty.");
            if (f.Length > MaxModSize) return BadRequest($"File '{f.FileName}' exceeds 512 MB.");
            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            if (!AllowedModExtensions.Contains(ext))
                return BadRequest($"File type '{ext}' not allowed. Accepted: {string.Join(", ", AllowedModExtensions)}");
        }

        // ── Validate / resolve mod group ──────────────────────────────────────
        ModGroup group;
        if (dto.ModGroupId.HasValue)
        {
            var existing = await db.ModGroups.FindAsync(dto.ModGroupId.Value);
            if (existing is null) return BadRequest("ModGroup not found.");
            if (existing.OwnerId.HasValue && existing.OwnerId != userId)
                return Forbid(); // only the original uploader can add versions
            group = existing;
        }
        else
        {
            group = new ModGroup { Author = username, OwnerId = userId };
            db.ModGroups.Add(group);
            await db.SaveChangesAsync(); // need Id before creating Mod
        }

        // ── Save preview image ────────────────────────────────────────────────
        string? previewName = null;
        if (previewImage is { Length: > 0 })
        {
            var imgExt = Path.GetExtension(previewImage.FileName).ToLowerInvariant();
            if (!AllowedImageExtensions.Contains(imgExt))
                return BadRequest($"Image type '{imgExt}' not allowed.");
            if (previewImage.Length > MaxImageSize)
                return BadRequest("Preview image exceeds 8 MB.");

            previewName = $"{Guid.NewGuid():N}{imgExt}";
            var previewDir = Path.Combine(env.WebRootPath ?? "wwwroot", "previews");
            Directory.CreateDirectory(previewDir);
            await using var imgStream = System.IO.File.Create(Path.Combine(previewDir, previewName));
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
            ModGroupId       = group.Id,
            Name             = dto.Name,
            Description      = dto.Description,
            Author           = username,
            Version          = dto.Version,
            Category         = dto.Category,
            PreviewImageName = previewName,
            UploadedAt       = DateTime.UtcNow,
            IsApproved       = true,
            UserId           = userId
        };
        db.Mods.Add(mod);
        await db.SaveChangesAsync(); // need mod.Id for ModFile FKs

        // ── Save each mod file ────────────────────────────────────────────────
        var uploadsDir = Path.Combine(env.WebRootPath ?? "wwwroot", "uploads");
        Directory.CreateDirectory(uploadsDir);

        for (int i = 0; i < files.Count; i++)
        {
            var f        = files[i];
            var ext      = Path.GetExtension(f.FileName).ToLowerInvariant();
            var safeName = $"{Guid.NewGuid():N}{ext}";
            var origName = i < originalNames.Count ? originalNames[i] : f.FileName;
            var installPath = i < installPaths.Count ? (installPaths[i] ?? "").Trim() : "";

            await using var stream = System.IO.File.Create(Path.Combine(uploadsDir, safeName));
            await f.CopyToAsync(stream);

            db.ModFiles.Add(new ModFile
            {
                ModId        = mod.Id,
                FileName     = safeName,
                OriginalName = origName,
                InstallPath  = installPath,
                FileSizeBytes = f.Length
            });
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Mod uploaded: {Name} v{Version} by {Author} ({FileCount} files)",
            mod.Name, mod.Version, mod.Author, files.Count);

        // Return with files populated
        return CreatedAtAction(nameof(GetMod), new { id = mod.Id },
            await db.Mods.Include(m => m.Files).FirstAsync(m => m.Id == mod.Id));
    }

    // POST /api/mods/{id}/download/{fileId}
    [HttpPost("{id:int}/download/{fileId:int}")]
    public async Task<IActionResult> Download(int id, int fileId)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null) return NotFound();

        var file = mod.Files.FirstOrDefault(f => f.Id == fileId);
        if (file is null) return NotFound("File not found in this mod version.");

        mod.DownloadCount++;
        await db.SaveChangesAsync();

        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "uploads", file.FileName);
        if (!System.IO.File.Exists(path))
            return NotFound("File not found on server.");

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, "application/octet-stream", file.OriginalName);
    }


    // PUT /api/mods/{id}  — edit metadata; owner or admin only
    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<ActionResult<Mod>> Edit(int id, [FromBody] ModEditDto dto)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null) return NotFound();

        var userId  = int.TryParse(User.FindFirstValue("sub"), out var uid) ? uid : (int?)null;
        var isAdmin = User.IsInRole("Admin");
        var group   = await db.ModGroups.FindAsync(mod.ModGroupId);
        if (!isAdmin && group?.OwnerId != null && group.OwnerId != userId) return Forbid();

        mod.Name        = dto.Name;
        mod.Description = dto.Description;
        mod.Version     = dto.Version;
        mod.Category    = dto.Category;
        await db.SaveChangesAsync();

        return Ok(mod);
    }

    // DELETE /api/mods/{id}  — owner or admin only
    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Delete(int id)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id);
        if (mod is null) return NotFound();

        var userId  = int.TryParse(User.FindFirstValue("sub"), out var uid) ? uid : (int?)null;
        var isAdmin = User.IsInRole("Admin");
        var group   = await db.ModGroups.FindAsync(mod.ModGroupId);
        if (!isAdmin && group?.OwnerId != null && group.OwnerId != userId) return Forbid();

        // Delete uploaded files from disk
        var uploadsDir = Path.Combine(env.WebRootPath ?? "wwwroot", "uploads");
        foreach (var f in mod.Files)
        {
            var p = Path.Combine(uploadsDir, f.FileName);
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
        }

        db.Mods.Remove(mod);

        // If this was the last version in the group, remove the group too
        var remaining = await db.Mods.CountAsync(m => m.ModGroupId == mod.ModGroupId && m.Id != id);
        if (remaining == 0 && group is not null)
            db.ModGroups.Remove(group);

        await db.SaveChangesAsync();
        return NoContent();
    }
}
