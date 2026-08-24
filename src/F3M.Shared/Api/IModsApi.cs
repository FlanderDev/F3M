using F3M.Shared;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using FlanderDev.RouteGen;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for mod browsing, editing, and downloading. RouteGen generates
/// ModsApiControllerBase (server) and HttpModsApi (client) from this — ModsController stays a
/// thin adapter over the base, ModsService holds the actual EF work.
///
/// Upload is deliberately NOT part of this contract — it binds via [FromForm]/IFormFileCollection
/// (multipart/form-data), which RouteGen's [Body] (JSON only) doesn't support. ModsController
/// keeps a normal hand-written [HttpPost] action for it alongside the generated-base overrides;
/// a controller isn't required to be 100% generated-base actions, it can mix both.
///
/// Two return types were corrected during migration, not just carried over as-is — both were
/// pre-existing mismatches between the old controller's *declared* action return type and what
/// it actually sent over the wire (harmless under the old loosely-typed HttpClient calls, but
/// RouteGen's generated client trusts the interface's declared type literally, so a mismatch
/// here would throw at deserialization time):
///   - the old `GetMod(string query)` claimed `ActionResult&lt;Mod&gt;` but actually returned an
///     array of mods; renamed to SearchMods and typed as List&lt;Mod&gt;, matching what every
///     existing caller already deserialized as.
///   - the old `GetCategories()` claimed `List&lt;(string, int)&gt;` but actually returned
///     `List&lt;string&gt;`; corrected to match, again matching what the existing caller already
///     deserialized as.
/// </summary>
[ApiRoute("api/mods", HttpClientName = Configuration.AppName)]
public interface IModsApi
{
    [Get]
    Task<ModListResult> GetMods(
        [Query] int page = 1,
        [Query] int pageSize = 18,
        [Query] string? search = null,
        [Query] string? category = null,
        [Query] SortBy sort = SortBy.Newest,
        CancellationToken ct = default);

    [Get("{id:int}")]
    Task<Mod> GetMod(int id, CancellationToken ct = default);

    [Get("{query}")]
    Task<List<Mod>> SearchMods(string query, CancellationToken ct = default);

    [Get("group/{groupId:int}/versions")]
    Task<ModVersionsResult> GetVersions(int groupId, CancellationToken ct = default);

    [Get("categories")]
    Task<List<string>> GetCategories(CancellationToken ct = default);

    [Post("{id:int}/download/{fileId:int}")]
    Task<Stream> DownloadFile(int id, int fileId, CancellationToken ct = default);

    [Put("{id:int}")]
    [Authorize]
    Task<Mod> Edit(int id, [Body] ModEditDto dto, CancellationToken ct = default);

    [Delete("{id:int}")]
    [Authorize]
    Task Delete(int id, CancellationToken ct = default);
}
