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
public partial class ModsService(AppDbContext db, IHttpContextAccessor httpContextAccessor, CatalogService catalog) : IModsApi
{
    private ClaimsPrincipal CurrentPrincipal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("ModsService was called outside of an HTTP request.");

    public async Task<ModListResult> GetMods(
        int page = 1, int pageSize = 18, string? search = null, string? category = null,
        SortBy sort = SortBy.Newest, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

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
        {
            var pattern = LikePattern(search.Trim());
            query = query.Where(m => EF.Functions.Like(m.Name, pattern, "\\"));
        }

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

        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new ModListResult { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    public async Task<Mod> GetMod(int id, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

        await ResolveDependenciesAsync([mod], ct);
        return mod;
    }

    public async Task<List<Mod>> SearchMods(string query, CancellationToken ct = default)
    {
        // This is used exclusively by the dependency picker (Upload.razor), which now selects
        // a logical mod (ModGroup) to depend on, not a specific version — so results are
        // deduped to one entry (the latest approved version) per group, rather than surfacing
        // every version of every matching mod as a separate pickable row.
        var latestIds = db.Mods
            .Where(m => m.IsApproved)
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var pattern = LikePattern(query.Trim());
        return await db.Mods
            .Where(m => latestIds.Contains(m.Id) && EF.Functions.Like(m.Name, pattern, "\\"))
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
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);

        await ResolveDependenciesAsync(versions, ct);
        return new ModVersionsResult { Group = group, Versions = versions };
    }

    public async Task<List<string>> GetCategories(CancellationToken ct = default)
    {
        // Category of each group = category of its latest version
        var latestIds = db.Mods
            .Where(m => m.IsApproved)
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        return await db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Select(m => m.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct)!;
    }

    public async Task<Stream> DownloadFile(int id, int fileId, CancellationToken ct = default)
    {
        var mod = await db.Mods.Include(m => m.Files).FirstOrDefaultAsync(m => m.Id == id, ct)
                  ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

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
        var mod = await db.Mods.Include(m => m.Files).Include(m => m.DependencyGroups)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new KeyNotFoundException($"No mod with id {id} was found.");

        var group = await db.ModGroups.FindAsync([mod.ModGroupId], ct);
        EnsureCanModify(group);

        mod.Name = dto.Name;
        mod.Description = dto.Description;
        mod.Version = dto.Version;
        mod.Category = dto.Category;

        var requestedIds = dto.DependencyGroupIds.Where(depId => depId != mod.ModGroupId).Distinct().ToList();
        var newDependencyGroups = requestedIds.Count > 0
            ? await db.ModGroups.Where(g => requestedIds.Contains(g.Id)).ToListAsync(ct)
            : [];

        mod.DependencyGroups.Clear();
        mod.DependencyGroups.AddRange(newDependencyGroups);

        await RecalculateLatestVersion(db, group?.Id, mod);
        await db.SaveChangesAsync(ct);
        // Metadata changed, so the published document must be rewritten. Placements never change here.
        await catalog.TryPublishVersionAsync(mod.Id, ct);
        await ResolveDependenciesAsync([mod], ct);
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
        await catalog.TryRemoveVersionAsync(mod.ModGroupId, mod.Id, ct);

        foreach (var path in diskFiles)
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

        var latestIds = db.Mods
            .Where(m => m.IsApproved && groupIds.Contains(m.ModGroupId))
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var latestByGroup = (await db.Mods
                .Where(m => latestIds.Contains(m.Id))
                .ToListAsync(ct))
            .ToDictionary(m => m.ModGroupId);

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
