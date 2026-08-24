using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated F95LinkApiControllerBase — no route attributes,
/// no route strings, no [FromBody] anywhere here; all of that comes from the generated base
/// (see obj/**/generated/RouteGen.Generators/.../Server_IF95LinkApi.g.cs after build), which is
/// itself derived from the attributes on IF95LinkApi. F95LinkService does the actual work and
/// never throws for expected outcomes — every code path already returns a fully-formed DTO, so
/// unlike ProfileController/AdminController there's no exception-to-ActionResult bridging
/// needed here at all.
/// </summary>
public class F95LinkController(IF95LinkApi f95LinkApi) : F95LinkApiControllerBase
{
    public override async Task<ActionResult<LinkF95StartResponse>> Start(LinkF95StartRequest request, CancellationToken ct)
        => Ok(await f95LinkApi.Start(request, ct));

    public override async Task<ActionResult<LinkF95PollResponse>> Check(string f95UserId, LinkF95PollRequest request, CancellationToken ct)
        => Ok(await f95LinkApi.Check(f95UserId, request, ct));
}
