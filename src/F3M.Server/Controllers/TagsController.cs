using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>Thin controller over the RouteGen-generated TagsApiControllerBase; TagService does the work and the checks.</summary>
public sealed class TagsController(ITagsApi tags) : TagsApiControllerBase
{
    public override async Task<ActionResult<List<TagDto>>> GetTags(CancellationToken ct)
        => Ok(await tags.GetTags(ct));

    public override Task<ActionResult<TagDto>> Create(TagEditDto dto, CancellationToken ct)
        => Run(() => tags.Create(dto, ct));

    public override Task<ActionResult<TagDto>> Update(int id, TagEditDto dto, CancellationToken ct)
        => Run(() => tags.Update(id, dto, ct));

    public override async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await tags.Delete(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    public override Task<ActionResult<TagDto>> Merge(int id, int targetId, CancellationToken ct)
        => Run(() => tags.Merge(id, targetId, ct));

    private async Task<ActionResult<TagDto>> Run(Func<Task<TagDto>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
