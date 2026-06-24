#if DEBUG
using F3M.Server.Data;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace F3M.Server.Controllers;

[ApiController]
public sealed class DebugController(AppDbContext db, ILogger<ModsController> logger) : ControllerBase
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
    public async Task<IActionResult> Upload([FromForm] ModUploadDto dto,
        [FromForm] IFormFileCollection files,
        [FromForm] List<string> installPaths,
        [FromForm] List<string> originalNames,
        IFormFile? previewImage)
    {
        if (!IsLocalNetwork(HttpContext.Connection.RemoteIpAddress))
            return Unauthorized("Access denied: Only local network allowed");

        var modsController = new ModsController(db, logger);

        try
        {
            await modsController.Upload(dto, files, installPaths, originalNames, previewImage);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return BadRequest("An error occurred while uploading the mod.");
        }

        return Ok("Local network access confirmed!");
    }
}

#endif