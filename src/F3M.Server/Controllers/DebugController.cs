#if DEBUG
using F3M.Server.Data;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace F3M.Server.Controllers;

[ApiController]
public sealed class DebugController(AppDbContext db) : ControllerBase
{
    private static bool IsLocalNetwork(IPAddress? ip)
    {
        if (ip == null) return false;

        byte[] bytes = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().GetAddressBytes() : ip.GetAddressBytes();

        // 10.0.0.0/8
        if (bytes[0] == 10) return true;

        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        // 127.0.0.0/8 (loopback)
        if (IPAddress.IsLoopback(ip)) return true;

        return false;
    }

    [HttpPost("/Upload")]
    public async Task<IActionResult> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage,
        [FromForm] string author)
    {
        if (!IsLocalNetwork(HttpContext.Connection.RemoteIpAddress))
            return Unauthorized("Access denied: Only local network allowed");


        Mod? mod = null;
        try
        {
            mod = await ModUploadAsync(dto, files, installPaths, originalNames, previewImage, author);
            //var modsController = new ModsController(db, logger);
            //await modsController.Upload(dto, files, installPaths, originalNames, previewImage);

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return BadRequest("An error occurred while uploading the mod.");
        }

        if (mod == null)
            Console.WriteLine();

        return Ok(mod?.ModGroupId);
    }

    private async Task<Mod?> ModUploadAsync(
        ModUploadDto dto,
        IFormFileCollection files,
        List<string> installPaths,
        List<string> originalNames,
        IFormFile? previewImage,
        string author)
    {
        ModGroup? group = null;
        if (dto.ModGroupId is { } modGroupId) // This is a new version of an existing mod
        {
            var existing = db.Mods.FirstOrDefault(m => m.ModGroupId == modGroupId);
            if (existing is null)
                return null; //BadRequest($"Mod with group ID {modGroupId} not found.");


            var existingModGroup = db.ModGroups.FirstOrDefault(m => m.Id == modGroupId);
            if (existingModGroup is not null)
                group = existingModGroup;
        }

        if (group is null) // ModGroup doesn't exist yet
        {
            group = new ModGroup { Author = dto.Name, OwnerId = -1 };
            db.ModGroups.Add(group);
            await db.SaveChangesAsync(); // need Id before creating Mod
        }

        var mod = new Mod
        {
            ModGroupId = group.Id,
            Name = dto.Name,
            Description = dto.Description,
            Author = author,
            Version = dto.Version,
            Category = dto.Category,
            PreviewImageName = "",
            UploadedAt = DateTime.UtcNow,
            IsApproved = true,
            UserId = -1
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
        return mod; //Ok(mod);
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

#endif