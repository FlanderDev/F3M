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
public sealed class ModsController(AppDbContext db, IModsApi modsApi, ModUploadService uploader) : ModsApiControllerBase
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

    public override async Task<ActionResult<ClaimModResult>> Claim(int groupId, CancellationToken ct)
    {
        try
        {
            return Ok(await modsApi.Claim(groupId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    public override async Task<IActionResult> DeleteGroup(int groupId, CancellationToken ct)
    {
        try
        {
            await modsApi.DeleteGroup(groupId, ct);
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
    }

    // ── Upload: new mod OR new version ────────────────────────────────────────
    // Form fields:
    //   Name, Description, Version, Category, ModGroupId (optional)
    //   GeneratedPaths[i].Pattern and GeneratedPaths[i].Kind (optional, plan 5.8)
    //   previewImage (optional IFormFile)
    //   files[]           — multiple mod files
    //   installPaths[]    — one install path per file (same index)
    //   originalNames[]   — original filenames (same index)
    //
    // ModUploadService does the checks and the storing. Non-blocking warnings are returned in the X-F3M-Warnings
    // header (URL-encoded, one per line).
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(Configuration.MaxTotalSize)]
    public async Task<ActionResult<Mod>> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage,
        CancellationToken ct)
    {
        var userId = Helper.GetUserId(User);
        if (userId is null)
            return BadRequest("User ID not found.");

        var owner = new UploadOwner(userId.Value, User.FindFirstValue(ClaimTypes.Name) ?? "unknown");
        var uploadFiles = files.Select((f, i) => new UploadFile(
            f,
            i < originalNames.Count ? originalNames[i] : null,
            i < installPaths.Count ? installPaths[i] : null)).ToList();

        var result = await uploader.UploadAsync(dto, uploadFiles, previewImage, owner, ct: ct);
        if (result.Forbidden)
            return Forbid();
        if (result.Mod is not { } uploaded)
            return BadRequest(result.Error);

        if (result.Warnings.Count > 0)
            Response.Headers["X-F3M-Warnings"] = Uri.EscapeDataString(string.Join('\n', result.Warnings));

        return CreatedAtAction(nameof(GetMod), new { id = uploaded.Id }, uploaded);
    }
}
