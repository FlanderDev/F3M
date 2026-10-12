using F3M.Server.Data;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>A mod that is on the site already: its group and the versions it has.</summary>
public sealed record ImportedMod(int GroupId, IReadOnlyList<string> Versions);

/// <summary>
/// Stores mods published elsewhere (F95zone) under the account of the user who posted them. Used by the import endpoint
/// for import/tools/upload.py (ImportController) and by the import admin page (F95ImportService); both go through
/// <see cref="ModUploadService"/>, so imports get the same checks, file inspection and catalog entries as the upload form.
///
/// An imported mod belongs to the F95zone user who posted it: to their account if they already have one, otherwise
/// it stays unclaimed (<see cref="ModGroup.UnclaimedOwnerId"/>) until they sign in through F95.
/// </summary>
public sealed class ModImportService(AppDbContext db, ModUploadService uploader)
{
    /// <summary>
    /// Stores one mod, or one version of it when <c>dto.ModGroupId</c> is set. A new version belongs to whoever owns
    /// the mod; the admin uploads it on their behalf. Files of type <see cref="UploadFile.AutoTemplate"/> get their
    /// type, and without an install path also their install path and archive folder, from their contents.
    /// </summary>
    /// <param name="author">The credit shown on the mod.</param>
    /// <param name="uploadedAt">When the version was published originally.</param>
    public async Task<UploadResult> ImportAsync(
        ModUploadDto dto,
        IEnumerable<UploadFile> files,
        IFormFile? previewImage,
        string author,
        DateTime? uploadedAt,
        UploadOrigin origin,
        CancellationToken ct)
    {
        int ownerId;
        if (dto.ModGroupId is { } groupId)
        {
            var group = await db.ModGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
            if (group is null)
                return UploadResult.Fail($"Mod with group ID {groupId} not found.");
            ownerId = group.OwnerId;
        }
        else
        {
            var existingUser = string.IsNullOrEmpty(origin.F95UserId)
                ? null
                : await db.Users.AsNoTracking().Where(u => u.F95UserId == origin.F95UserId).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            ownerId = existingUser ?? ModGroup.UnclaimedOwnerId;
        }

        var uploadFiles = files.Select(f => f.Recognise(dto.Name)).ToList();
        // Nobody reviews an import's layout, so readmes and examples are made optional on their own, as the upload
        // form suggests to its users.
        var targets = uploadFiles.SelectMany(f => f.Targets()).ToList();
        dto.OptionalGroups = [.. dto.OptionalGroups, .. ModTemplates.SuggestOptionalGroups(dto.OptionalGroups, targets)];

        var when = uploadedAt is { } at ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : (DateTime?)null;
        return await uploader.UploadAsync(dto, uploadFiles, previewImage, new UploadOwner(ownerId, author), when, origin, ct);
    }

    /// <summary>The mod with this name and author credit, if it was imported already.</summary>
    public async Task<ImportedMod?> FindAsync(string name, string author, CancellationToken ct)
    {
        var versions = await db.Mods
            .Where(m => m.Name == name && m.Author == author)
            .OrderBy(m => m.UploadedAt)
            .Select(m => new { m.ModGroupId, m.Version })
            .ToListAsync(ct);

        return versions.Count == 0 ? null : new ImportedMod(versions[0].ModGroupId, [.. versions.Select(v => v.Version)]);
    }
}
