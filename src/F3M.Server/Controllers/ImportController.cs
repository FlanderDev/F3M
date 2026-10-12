using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Admin-only bulk import of mods published elsewhere (import/tools/upload.py). Each call stores one version through
/// <see cref="ModImportService"/>, which the import admin page uses too. Hand-written rather than RouteGen-generated
/// because it binds multipart/form-data, like ModsController.Upload.
/// </summary>
[ApiController]
[Route("api/import")]
[Authorize(Roles = AppRoles.Admin)]
public sealed class ImportController(ModImportService importer, ILogger<ImportController> logger) : ControllerBase
{
    /// <summary>
    /// Stores one mod, or one version of it when <c>ModGroupId</c> is set. Same form fields as the upload form, plus:
    /// <paramref name="author"/> (credit shown on the mod), <paramref name="uploadedAt"/> (original publication date),
    /// <paramref name="f95OwnerUserId"/> and <paramref name="f95OwnerName"/> (who posted it on F95zone) and
    /// <paramref name="sourceUrl"/> (where). Returns the group id, or the reason as text when the upload is rejected;
    /// 422 with the warnings when the files don't match their types and <c>AcceptWarnings</c> is not set.
    /// A file whose type is <see cref="UploadFile.AutoTemplate"/> gets its type, and without an install path also its
    /// install path and archive folder, from its contents (<see cref="UploadFile.Recognise"/>).
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(Configuration.MaxTotalSize)]
    public async Task<IActionResult> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        [FromForm] List<string>? archiveRoots,
        [FromForm] List<string>? templates,
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

        var uploadFiles = UploadFile.FromForm(files, originalNames, installPaths, archiveRoots, templates);
        var origin = new UploadOrigin(NullIfEmpty(f95OwnerUserId), NullIfEmpty(f95OwnerName), NullIfEmpty(sourceUrl));

        var result = await importer.ImportAsync(dto, uploadFiles, previewImage, author, uploadedAt, origin, ct);
        if (result.Unconfirmed is { } unconfirmed)
            return UnprocessableEntity(new UploadWarningsResult { Warnings = [.. unconfirmed] });
        if (result.Mod is not { } mod)
            return BadRequest(result.Error ?? "The upload was rejected.");

        logger.LogInformation("Imported {Name} {Version} (group {GroupId}).", mod.Name, mod.Version, mod.ModGroupId);
        return Ok(mod.ModGroupId);
    }

    /// <summary>
    /// The mod with this name and author credit, if it was imported already: its group id and stored versions.
    /// Lets the import script skip what is there and add only missing versions.
    /// </summary>
    [HttpGet("mod")]
    public async Task<IActionResult> FindMod([FromQuery] string name, [FromQuery] string author, CancellationToken ct)
    {
        var found = await importer.FindAsync(name, author, ct);
        return found is null ? NotFound() : Ok(new { groupId = found.GroupId, versions = found.Versions });
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
