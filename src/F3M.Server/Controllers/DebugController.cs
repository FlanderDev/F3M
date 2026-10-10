#if DEBUG
using F3M.Server.Services;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace F3M.Server.Controllers;

/// <summary>
/// Debug-build helper for uploading a mod without signing in. Only reachable from the local network.
/// Uploads go through <see cref="ModUploadService"/>, so they get the same checks, file inspection and catalog entries
/// as the upload form. Bulk imports with their F95 uploader use the admin-only ImportController instead.
/// </summary>
[ApiController]
public sealed class DebugController(ModUploadService uploader) : ControllerBase
{
    private static bool IsLocalNetwork(IPAddress? ip)
    {
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal) return true;

        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10                                     // 10.0.0.0/8
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) // 172.16.0.0/12
               || (bytes[0] == 192 && bytes[1] == 168);            // 192.168.0.0/16
    }

    /// <summary>
    /// Uploads one mod, or one version of it when <c>ModGroupId</c> is set. Same form fields as the upload form,
    /// plus <paramref name="author"/> and an optional <paramref name="uploadedAt"/> (defaults to now).
    /// The mod is unclaimed. Returns the group id; the reason as text when the upload is rejected.
    /// </summary>
    [HttpPost("/Upload")]
    public async Task<IActionResult> Upload(
        [FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage,
        [FromForm] string author,
        [FromForm] DateTime? uploadedAt,
        CancellationToken ct)
    {
        if (!IsLocalNetwork(HttpContext.Connection.RemoteIpAddress))
            return Unauthorized("Access denied: Only local network allowed");

        var uploadFiles = files.Select((f, i) => new UploadFile(
            f,
            i < originalNames.Count ? originalNames[i] : null,
            i < installPaths.Count ? installPaths[i] : null)).ToList();

        var when = uploadedAt is { } at ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : (DateTime?)null;
        var result = await uploader.UploadAsync(dto, uploadFiles, previewImage, new UploadOwner(ModGroup.UnclaimedOwnerId, author), when, ct: ct);
        if (result.Forbidden)
            return Forbid();
        if (result.Mod is not { } mod)
            return BadRequest(result.Error);

        return Ok(mod.ModGroupId);
    }
}

#endif
