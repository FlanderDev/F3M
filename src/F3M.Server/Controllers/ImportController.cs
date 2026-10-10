using F3M.Server.Data;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Controllers;

/// <summary>
/// Admin-only bulk import of mods published elsewhere (import/tools/upload.py). Each call stores one version through
/// <see cref="ModUploadService"/>, so imports get the same checks, file inspection and catalog entries as the upload
/// form. Hand-written rather than RouteGen-generated because it binds multipart/form-data, like ModsController.Upload.
///
/// An imported mod belongs to the F95zone user who posted it: to their account if they already have one, otherwise
/// it stays unclaimed (<see cref="ModGroup.UnclaimedOwnerId"/>) until they sign in through F95.
/// </summary>
[ApiController]
[Route("api/import")]
[Authorize(Roles = AppRoles.Admin)]
public sealed class ImportController(AppDbContext db, ModUploadService uploader, ILogger<ImportController> logger) : ControllerBase
{
    /// <summary>
    /// Stores one mod, or one version of it when <c>ModGroupId</c> is set. Same form fields as the upload form, plus:
    /// <paramref name="author"/> (credit shown on the mod), <paramref name="uploadedAt"/> (original publication date),
    /// <paramref name="f95OwnerUserId"/> and <paramref name="f95OwnerName"/> (who posted it on F95zone) and
    /// <paramref name="sourceUrl"/> (where). Returns the group id, or the reason as text when the upload is rejected.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(Configuration.MaxTotalSize)]
    public async Task<IActionResult> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage,
        [FromForm] string author,
        [FromForm] DateTime? uploadedAt,
        [FromForm] string? f95OwnerUserId,
        [FromForm] string? f95OwnerName,
        [FromForm] string? sourceUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author))
            return BadRequest("An author is required.");
        if (f95OwnerUserId is { Length: > 0 } && !f95OwnerUserId.All(char.IsAsciiDigit))
            return BadRequest("f95OwnerUserId must be a numeric F95zone user id.");

        // A new version belongs to whoever owns the mod; the admin uploads it on their behalf.
        int ownerId;
        if (dto.ModGroupId is { } groupId)
        {
            var group = await db.ModGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
            if (group is null)
                return BadRequest($"Mod with group ID {groupId} not found.");
            ownerId = group.OwnerId;
        }
        else
        {
            var existingUser = string.IsNullOrEmpty(f95OwnerUserId)
                ? null
                : await db.Users.AsNoTracking().Where(u => u.F95UserId == f95OwnerUserId).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            ownerId = existingUser ?? ModGroup.UnclaimedOwnerId;
        }

        var uploadFiles = files.Select((f, i) => new UploadFile(
            f,
            i < originalNames.Count ? originalNames[i] : null,
            i < installPaths.Count ? installPaths[i] : null)).ToList();

        var when = uploadedAt is { } at ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : (DateTime?)null;
        var origin = new UploadOrigin(NullIfEmpty(f95OwnerUserId), NullIfEmpty(f95OwnerName), NullIfEmpty(sourceUrl));

        var result = await uploader.UploadAsync(dto, uploadFiles, previewImage, new UploadOwner(ownerId, author), when, origin, ct);
        if (result.Mod is not { } mod)
            return BadRequest(result.Error ?? "The upload was rejected.");

        logger.LogInformation("Imported {Name} {Version} (group {GroupId}, owner {OwnerId}).", mod.Name, mod.Version, mod.ModGroupId, ownerId);
        return Ok(mod.ModGroupId);
    }

    /// <summary>
    /// The mod with this name and author credit, if it was imported already: its group id and stored versions.
    /// Lets the import script skip what is there and add only missing versions.
    /// </summary>
    [HttpGet("mod")]
    public async Task<IActionResult> FindMod([FromQuery] string name, [FromQuery] string author, CancellationToken ct)
    {
        var versions = await db.Mods
            .Where(m => m.Name == name && m.Author == author)
            .OrderBy(m => m.UploadedAt)
            .Select(m => new { m.ModGroupId, m.Version })
            .ToListAsync(ct);

        if (versions.Count == 0)
            return NotFound();

        return Ok(new { groupId = versions[0].ModGroupId, versions = versions.Select(v => v.Version) });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
