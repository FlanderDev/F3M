using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IModsApi"/>, minus Upload — that one stays directly
/// on ModsController as a hand-written [FromForm] action (multipart/form-data isn't something
/// RouteGen's [Body] covers). ModsController is a thin adapter over this for everything else,
/// same pattern as ProfileService/AdminService.
/// </summary>
public partial class ModsService(
    AppDbContext db, IHttpContextAccessor httpContextAccessor, CatalogService catalog, ModOwnershipService ownership,
    TagService tagService, SiteSettings settings, ILogger<ModsService> logger) : IModsApi
{
    private const int MinSearchLength = 3;
    private const int MaxSearchResults = 50;

    private ClaimsPrincipal CurrentPrincipal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("ModsService was called outside of an HTTP request.");

    public async Task<ModListResult> GetMods(
        int page = 1, int pageSize = 18, string? search = null,
        SortBy sort = SortBy.Newest, string? tags = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Latest version per group: the one RecalculateLatestVersion marked (highest version number), the same one the
        // signed catalog and so the desktop app offer. Upload dates would disagree after a fix for an older version.
        var query = db.Mods
            .Where(m => m.IsApproved && m.IsLatestVersion)
            .Include(m => m.Files)
            .AsQueryable();

        // Suggestions whose votes reached the limit count like the mods' own tags.
        var counting = tagService.CountingSuggestions(await settings.TagSuggestionThresholdAsync(ct));

        if (!string.IsNullOrWhiteSpace(search))
        {
            // The name, or a tag (or a tag including the mod's): "texture" finds the mods tagged Textures too.
            var pattern = LikePattern(search.Trim());
            var matchingTags = await tagService.MatchingTagsAsync(search.Trim(), ct);
            query = query.Where(m => EF.Functions.Like(m.Name, pattern, "\\")
                                     || db.ModGroupTags.Any(t => t.ModGroupId == m.ModGroupId && matchingTags.Contains(t.TagId))
                                     || counting.Any(s => s.ModGroupId == m.ModGroupId && matchingTags.Contains(s.TagId!.Value)));
        }

        if (!string.IsNullOrWhiteSpace(tags))
        {
            var sets = await tagService.FilterSetsAsync(tags.Split(TagRules.ListSeparator), ct);
            if (sets is null)
                return new ModListResult { Page = page, PageSize = pageSize };

            foreach (var set in sets)
            {
                query = query.Where(m => db.ModGroupTags.Any(t => t.ModGroupId == m.ModGroupId && set.Contains(t.TagId))
                                         || counting.Any(s => s.ModGroupId == m.ModGroupId && set.Contains(s.TagId!.Value)));
            }
        }

        // The id breaks ties (many mods share a download count), so pages never overlap or skip a mod when the
        // browse page appends them one after another.
        query = sort switch
        {
            SortBy.Newest => query.OrderByDescending(m => m.UploadedAt).ThenByDescending(m => m.Id),
            SortBy.Oldest => query.OrderBy(m => m.UploadedAt).ThenBy(m => m.Id),
            SortBy.DownloadsDesc => query.OrderByDescending(m => m.DownloadCount).ThenByDescending(m => m.Id),
            SortBy.DownloadsAsc => query.OrderBy(m => m.DownloadCount).ThenBy(m => m.Id),
            SortBy.NameAsc => query.OrderBy(m => m.Name).ThenBy(m => m.Id),
            SortBy.NameDesc => query.OrderByDescending(m => m.Name).ThenByDescending(m => m.Id),
            _ => query.OrderByDescending(m => m.UploadedAt).ThenByDescending(m => m.Id)
        };

        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        await tagService.FillTagIdsAsync(items, ct);
        await FillCardInfoAsync(db, items, ct);
        return new ModListResult { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    /// <summary>What the mod cards show beyond the version itself: when the mod first came out, and whether it's unclaimed.</summary>
    public static async Task FillCardInfoAsync(AppDbContext db, IReadOnlyCollection<Mod> mods, CancellationToken ct = default)
    {
        var groupIds = mods.Select(m => m.ModGroupId).Distinct().ToList();
        if (groupIds.Count == 0)
            return;

        var firstUploads = await db.Mods
            .Where(m => groupIds.Contains(m.ModGroupId))
            .GroupBy(m => m.ModGroupId)
            .Select(g => new { g.Key, First = g.Min(m => m.UploadedAt) })
            .ToDictionaryAsync(g => g.Key, g => g.First, ct);
        var unclaimed = (await db.ModGroups
                .Where(g => groupIds.Contains(g.Id) && g.OwnerId == ModGroup.UnclaimedOwnerId)
                .Select(g => g.Id)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var mod in mods)
        {
            mod.FirstUploadedAt = firstUploads.TryGetValue(mod.ModGroupId, out var first) ? first : null;
            mod.IsUnclaimed = unclaimed.Contains(mod.ModGroupId);
        }
    }

    public async Task<Mod> GetMod(int id, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups).Include(m => m.OptionalGroups)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

        await ResolveDependenciesAsync([mod], ct);
        await tagService.FillTagIdsAsync([mod], ct);
        mod.ShippedPaths = await ShippedPathsAsync(mod, ct);
        return mod;
    }

    /// <summary>Every game-relative path the version's files install, from their stored entries.</summary>
    private async Task<List<string>> ShippedPathsAsync(Mod mod, CancellationToken ct)
    {
        var fileIds = mod.Files.Select(f => f.Id).ToList();
        return await db.ModFileEntries.Where(e => fileIds.Contains(e.ModFileId)).Select(e => e.To).Distinct().ToListAsync(ct);
    }

    public async Task<List<Mod>> SearchMods(string query, CancellationToken ct = default)
    {
        // This is used exclusively by the dependency picker (Upload.razor), which now selects
        // a logical mod (ModGroup) to depend on, not a specific version — so results are
        // deduped to one entry (the latest approved version) per group, rather than surfacing
        // every version of every matching mod as a separate pickable row.
        // The picker asks from three characters on; anything shorter would list the whole site.
        var text = query?.Trim() ?? string.Empty;
        if (text.Length < MinSearchLength)
            return [];

        var pattern = LikePattern(text);
        return await db.Mods
            .Where(m => m.IsApproved && m.IsLatestVersion && EF.Functions.Like(m.Name, pattern, "\\"))
            .OrderBy(m => m.Name)
            .Take(MaxSearchResults)
            .ToListAsync(ct);
    }

    public async Task<ModVersionsResult> GetVersions(int groupId, CancellationToken ct = default)
    {
        var group = await db.ModGroups.FindAsync([groupId], ct)
                    ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");

        var versions = await db.Mods
            .Where(m => m.ModGroupId == groupId && m.IsApproved)
            .Include(m => m.Files)
            .Include(m => m.DependencyGroups)
            .Include(m => m.OptionalGroups)
            // The latest version first (the page and the desktop button treat the first one as "latest"), then by date.
            .OrderByDescending(m => m.IsLatestVersion)
            .ThenByDescending(m => m.UploadedAt)
            .ToListAsync(ct);

        await ResolveDependenciesAsync(versions, ct);
        await tagService.FillTagIdsAsync(versions, ct);

        // The owner's public names, for the profile links on the mod page. An unclaimed import has no owner yet.
        var owner = group.IsUnclaimed
            ? null
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == group.OwnerId)
                .Select(u => new ModOwnerDto { Username = u.UserName ?? string.Empty, F95UserId = u.F95UserId, F95Username = u.F95Username })
                .FirstOrDefaultAsync(ct);

        return new ModVersionsResult { Group = group, Versions = versions, Owner = owner };
    }

    public async Task<Stream> DownloadFile(int id, int fileId, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id && m.IsApproved, ct)
                  ?? throw new KeyNotFoundException($"No mod with id {id} was found or is not approved.");

        var file = mod.Files.FirstOrDefault(f => f.Id == fileId)
                   ?? throw new KeyNotFoundException("File not found in this mod version.");

        var path = Path.Combine(Assets.Files, file.FileName);
        if (!File.Exists(path))
            throw new KeyNotFoundException("File not found on server.");

        mod.DownloadCount++;
        await db.SaveChangesAsync(ct);

        // Caller (controller) wraps this in a FileContentResult with the right filename/content
        // type — ModFile.OriginalName never leaves the server via this stream, the client
        // already knows it locally (it's downloading a file it can already see in the mod's
        // file list), same as before this migration.
        return File.OpenRead(path);
    }

    public async Task<Mod> Edit(int id, ModEditDto dto, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups).Include(m => m.OptionalGroups)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

        var group = await db.ModGroups.FindAsync([mod.ModGroupId], ct);
        EnsureCanModify(group);

        mod.Name = dto.Name;
        mod.Description = dto.Description;
        mod.Version = dto.Version;

        // Optional groups: the same rules as on upload, against the files this version ships.
        if (dto.OptionalGroups is not null)
        {
            if (!OptionalFileRules.TryNormalize(dto.OptionalGroups, out var groups, out var groupError))
                throw new InvalidDataException(groupError);

            var report = OptionalFileRules.Analyse(groups, await ShippedPathsAsync(mod, ct));
            if (report.Errors.Count > 0)
                throw new InvalidDataException(report.Errors[0]);

            db.ModOptionalGroups.RemoveRange(mod.OptionalGroups);
            mod.OptionalGroups = [.. groups.Select(g => new ModOptionalGroup { ModId = mod.Id, Name = g.Name, Patterns = g.Patterns })];
        }

        // Preview image: written now; the replaced one is deleted once the change is saved and nothing else uses it.
        var oldPreview = mod.PreviewImageName;
        string? newPreviewPath = null;
        if (dto.PreviewImageBase64 is { Length: > 0 } base64)
        {
            var extension = Path.GetExtension(dto.PreviewImageFileName ?? string.Empty).ToLowerInvariant();
            if (!Configuration.AllowedThumbnailExtension.Contains(extension))
                throw new InvalidDataException($"Image type '{extension}' not allowed. Use {string.Join(", ", Configuration.AllowedThumbnailExtension)}.");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("The preview image could not be read.");
            }

            if (bytes.Length == 0)
                throw new InvalidDataException("The preview image is empty.");
            if (bytes.Length > Configuration.MaxImageSize)
                throw new InvalidDataException("Preview image exceeds 8 MB.");

            // magic bytes decide the format and the extension it is stored with (AVIF becomes WebP).
            (bytes, extension) = PreviewImages.Normalize(bytes);

            var name = $"{Guid.NewGuid():N}{extension}";
            newPreviewPath = Path.Combine(Assets.Images, name);
            await File.WriteAllBytesAsync(newPreviewPath, bytes, ct);
            mod.PreviewImageName = name;
        }
        else if (dto.RemovePreviewImage)
        {
            mod.PreviewImageName = null;
        }

        var requestedIds = dto.DependencyGroupIds.Where(depId => depId != mod.ModGroupId).Distinct().ToList();
        var newDependencyGroups = requestedIds.Count > 0
            ? await db.ModGroups.Where(g => requestedIds.Contains(g.Id)).ToListAsync(ct)
            : [];

        mod.DependencyGroups.Clear();
        mod.DependencyGroups.AddRange(newDependencyGroups);

        // Tags belong to the whole mod. The index the catalog publishes them in is rewritten with the version below.
        if (dto.TagIds is not null)
            await tagService.SetGroupTagsAsync(mod.ModGroupId, dto.TagIds, Helper.GetUserId(CurrentPrincipal), ct);

        await RecalculateLatestVersion(db, group?.Id, mod);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (newPreviewPath is not null)
                File.Delete(newPreviewPath);
            throw;
        }

        // Older versions of the group may still show the replaced preview (versions inherit it on upload).
        if (oldPreview is { Length: > 0 } && oldPreview != mod.PreviewImageName
            && !await db.Mods.AnyAsync(m => m.PreviewImageName == oldPreview, ct))
        {
            try { File.Delete(Path.Combine(Assets.Images, oldPreview)); }
            catch (IOException) { /* an orphaned image is harmless */ }
        }

        logger.LogInformation("Mod {ModId} ({Name} v{Version}, group {GroupId}) was edited.", mod.Id, mod.Name, mod.Version, mod.ModGroupId);

        // A new version number can make another version the latest, whose files' types the mod then has.
        await tagService.SyncTypeTagsAsync(mod.ModGroupId, ct);

        // Metadata or optional groups changed, so the published document must be rewritten. Files never change here.
        await catalog.TryPublishVersionAsync(mod.Id, ct);
        await ResolveDependenciesAsync([mod], ct);
        await tagService.FillTagIdsAsync([mod], ct);
        mod.ShippedPaths = await ShippedPathsAsync(mod, ct);
        return mod;
    }

    public async Task Delete(int id, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id, ct)
                  ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

        var group = await db.ModGroups.FindAsync([mod.ModGroupId], ct);
        EnsureCanModify(group);

        var remaining = await db.Mods.CountAsync(m => m.ModGroupId == mod.ModGroupId && m.Id != id, ct);

        if (remaining == 0 && group is not null)
        {
            // This would delete the whole group (last remaining version) — refuse if any other
            // mod depends on it. Deleting a non-last version is always fine now, regardless of
            // dependencies, since dependencies point at the group, not this specific row.
            var isDependedUpon = await db.Mods
                .AnyAsync(m => m.DependencyGroups.Any(g => g.Id == mod.ModGroupId), ct);

            if (isDependedUpon)
                throw new InvalidOperationException(
                    "Cannot delete the last version of this mod — other mods depend on it.");
        }

        var diskFiles = mod.Files.Select(f => Path.Combine(Assets.Files, f.FileName)).ToList();

        db.Mods.Remove(mod);

        // If this was the last version in the group, remove the group too
        if (remaining == 0 && group is not null)
            db.ModGroups.Remove(group);

        await RecalculateLatestVersion(db, group?.Id, excludeModId: mod.Id);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(remaining == 0
                ? "Mod {ModId} ({Name} v{Version}) was deleted, and with it its group {GroupId} (it was the last version)."
                : "Mod {ModId} ({Name} v{Version}) was deleted from group {GroupId}.",
            mod.Id, mod.Name, mod.Version, mod.ModGroupId);
        if (remaining > 0)
            await tagService.SyncTypeTagsAsync(mod.ModGroupId, ct);
        await catalog.TryRemoveVersionAsync(mod.ModGroupId, mod.Id, ct);

        foreach (var path in diskFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* an orphaned file is harmless, a failed request is not */ }
        }
    }

    public async Task<ClaimModResult> Claim(int groupId, CancellationToken ct = default)
    {
        var userId = Helper.GetUserId(CurrentPrincipal)
                     ?? throw new UnauthorizedAccessException("No authenticated user.");
        var user = await db.Users.FindAsync([userId], ct)
                   ?? throw new UnauthorizedAccessException("The signed-in account no longer exists.");

        return await ownership.ClaimAsync(groupId, user, ct);
    }

    public async Task DeleteGroup(int groupId, CancellationToken ct = default)
    {
        var group = await db.ModGroups.FindAsync([groupId], ct)
                    ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");
        EnsureCanModify(group);

        var versions = await db.Mods.Include(m => m.Files).Where(m => m.ModGroupId == groupId).ToListAsync(ct);

        // Other mods may depend on this one. Their dependency on it goes; the rest of them stays.
        var dependents = await db.Mods.Include(m => m.DependencyGroups)
            .Where(m => m.ModGroupId != groupId && m.DependencyGroups.Any(g => g.Id == groupId))
            .ToListAsync(ct);
        foreach (var dependent in dependents)
            dependent.DependencyGroups.RemoveAll(g => g.Id == groupId);

        var diskFiles = versions.SelectMany(m => m.Files).Select(f => Path.Combine(Assets.Files, f.FileName)).ToList();
        var previews = versions.Select(m => m.PreviewImageName).OfType<string>().Where(n => n.Length > 0).Distinct().ToList();

        db.Mods.RemoveRange(versions);
        db.ModGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Mod group {GroupId} ({Name}) was deleted with {Count} versions; {Dependents} other mods lost it as a dependency.",
            groupId, versions.FirstOrDefault(v => v.IsLatestVersion)?.Name ?? versions.FirstOrDefault()?.Name, versions.Count, dependents.Count);

        foreach (var version in versions)
            await catalog.TryRemoveVersionAsync(groupId, version.Id, ct);
        // Their published documents still list the removed dependency.
        foreach (var dependent in dependents)
            await catalog.TryPublishVersionAsync(dependent.Id, ct);

        // Previews can be shared with other groups only through an inherited name, which stays within a group.
        var stillUsed = await db.Mods.Where(m => m.PreviewImageName != null && previews.Contains(m.PreviewImageName))
            .Select(m => m.PreviewImageName!).ToListAsync(ct);
        foreach (var path in diskFiles.Concat(previews.Except(stillUsed).Select(n => Path.Combine(Assets.Images, n))))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* an orphaned file is harmless, a failed request is not */ }
        }
    }

    /// <summary>
    /// Populates <see cref="Mod.Dependencies"/> (the resolved display view) for each mod in
    /// <paramref name="mods"/> from its persisted <see cref="Mod.DependencyGroups"/> — batched
    /// into a single query across all input mods rather than resolving one group at a time, so
    /// GetVersions (which can return several versions at once) doesn't turn into an N+1.
    /// Silently omits a dependency if its group currently has no approved version to resolve to
    /// (e.g. all versions pending approval) rather than failing the whole request.
    /// </summary>
    private async Task ResolveDependenciesAsync(IReadOnlyCollection<Mod> mods, CancellationToken ct)
    {
        var groupIds = mods.SelectMany(m => m.DependencyGroups.Select(g => g.Id)).Distinct().ToList();
        if (groupIds.Count == 0)
            return;

        var latestByGroup = (await db.Mods
                .Where(m => m.IsApproved && m.IsLatestVersion && groupIds.Contains(m.ModGroupId))
                .ToListAsync(ct))
            .GroupBy(m => m.ModGroupId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var mod in mods)
        {
            mod.Dependencies = [.. mod.DependencyGroups
                .Select(g => latestByGroup.GetValueOrDefault(g.Id))
                .Where(m => m is not null)
                .Select(m => m!)];
        }
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> (mapped to 403 Forbid by the controller —
    /// [Authorize] on the interface already rules out the unauthenticated/401 case before this
    /// runs) unless the current user owns the mod's group or is an Admin.
    ///
    /// A missing ModGroup record (<paramref name="group"/> is null) is denied for non-admins; it used
    /// to be silently allowed, which made orphaned mods editable by any signed-in user.
    /// </summary>
    private void EnsureCanModify(ModGroup? group)
    {
        var userId = Helper.GetUserId(CurrentPrincipal);
        var isAdmin = CurrentPrincipal.IsInRole(AppRoles.Admin);

        if (!isAdmin && (group is null || group.OwnerId != userId))
            throw new UnauthorizedAccessException("You do not own this mod.");
    }

    /// <summary>
    /// Also called directly by ModsController.Upload — that action stays hand-written (outside
    /// IModsApi) since it's multipart/form-data, but there's no reason to duplicate this logic
    /// just because of that; it's a plain static helper either way.
    /// </summary>
    internal static async Task RecalculateLatestVersion(
        AppDbContext appDbContext, int? modGroupId, Mod? incoming = null, int? excludeModId = null)
    {
        var existing = await appDbContext.Mods.Where(m => m.ModGroupId == modGroupId).ToListAsync();
        var candidates = existing.Where(m => m.Id != excludeModId).ToList();

        if (incoming is not null && incoming.Id != excludeModId && candidates.All(m => m.Id != incoming.Id || incoming.Id == 0))
            candidates.Add(incoming);

        if (candidates.Count == 0)
            return;

        var latest = candidates
            .OrderByDescending(m => ParseVersion(m.Version))
            .ThenByDescending(m => m.UploadedAt)
            .First();

        foreach (var m in candidates)
            m.IsLatestVersion = ReferenceEquals(m, latest);
        }

    internal static Version ParseVersion(string? text)
    {
        var match = VersionPattern().Match(text ?? string.Empty);
        if (!match.Success)
            return new Version(0, 0);

        var numeric = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return Version.TryParse(numeric, out var version) ? version : new Version(0, 0);
    }

    [GeneratedRegex(@"\d+(\.\d+){0,3}")]
    private static partial Regex VersionPattern();

    private static string LikePattern(string input) =>
        "%" + input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
