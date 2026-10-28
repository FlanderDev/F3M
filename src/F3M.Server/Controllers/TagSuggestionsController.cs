using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>Thin controller over the RouteGen-generated TagSuggestionsApiControllerBase; TagSuggestionService does the work.</summary>
public sealed class TagSuggestionsController(ITagSuggestionsApi suggestions, ILogger<TagSuggestionsController> logger)
    : TagSuggestionsApiControllerBase
{
    public override Task<ActionResult<ModTagSuggestions>> GetForMod(int groupId, CancellationToken ct)
        => Run(() => suggestions.GetForMod(groupId, ct));

    public override Task<ActionResult<TagSuggestionDto>> Suggest(int groupId, TagSuggestRequest request, CancellationToken ct)
        => Run(() => suggestions.Suggest(groupId, request, ct));

    public override Task<ActionResult<TagSuggestionDto>> Vote(int id, TagVoteRequest vote, CancellationToken ct)
        => Run(() => suggestions.Vote(id, vote, ct));

    public override Task<ActionResult<TagSuggestionDto>> Approve(int id, CancellationToken ct)
        => Run(() => suggestions.Approve(id, ct));

    public override Task<ActionResult<TagSuggestionDto>> Reject(int id, CancellationToken ct)
        => Run(() => suggestions.Reject(id, ct));

    public override async Task<ActionResult<List<TagSuggestionAdminDto>>> GetQueue(TagSuggestionState? state, CancellationToken ct)
        => Ok(await suggestions.GetQueue(state, ct));

    public override async Task<ActionResult<int>> GetOpenCount(CancellationToken ct)
        => Ok(await suggestions.GetOpenCount(ct));

    public override Task<ActionResult<TagSuggestionDto>> CreateTag(int id, TagEditDto tag, CancellationToken ct)
        => Run(() => suggestions.CreateTag(id, tag, ct));

    public override async Task<ActionResult<int>> ApproveCounting(CancellationToken ct)
        => Ok(await suggestions.ApproveCounting(ct));

    public override async Task<ActionResult<TagSuggestionLimits>> GetLimits(CancellationToken ct)
        => Ok(await suggestions.GetLimits(ct));

    public override Task<ActionResult<TagSuggestionLimits>> SetLimits(TagSuggestionLimits limits, CancellationToken ct)
        => Run(() => suggestions.SetLimits(limits, ct));

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
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
        catch (UnauthorizedAccessException ex)
        {
            // The page only offers deciding to owners and admins, so this is a hand-made request.
            logger.LogWarning("Refused a tag suggestion action: {Reason}", ex.Message);
            return Forbid();
        }
    }
}
