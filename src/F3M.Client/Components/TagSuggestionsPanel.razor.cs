using F3M.Client.Shared;
using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components;

public partial class TagSuggestionsPanel
{
    [Parameter, EditorRequired] public int GroupId { get; set; }

    /// <summary>The mod's tags: they, and the tags they count as, can't be suggested.</summary>
    [Parameter, EditorRequired] public IReadOnlyCollection<int> TagIds { get; set; } = [];

    /// <summary>A suggestion was approved: the tag is the mod's now.</summary>
    [Parameter] public EventCallback<int> TagApproved { get; set; }

    /// <summary>The admin queue, where requests for new tags are turned into tags.</summary>
    private static string SuggestionQueueUrl => $"{Paths.AdminTags}?view=suggestions";

    private ModTagSuggestions? data;
    private string? loadError;
    private string? error;
    private int? busyId;

    private bool suggesting;
    private bool requesting;
    private bool sending;
    private List<int> pickedTag = [];
    private string newTagName = string.Empty;
    private string reason = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        Directory.Changed += OnTagsChanged;
        await Directory.LoadAsync();
        await LoadAsync();
    }

    private void OnTagsChanged() => InvokeAsync(StateHasChanged);

    private async Task LoadAsync()
    {
        try
        {
            data = await Api.GetForMod(GroupId);
            loadError = null;
        }
        catch (Exception ex)
        {
            loadError = $"The suggestions could not be loaded: {ApiErrors.Describe(ex)}";
        }
    }

    private string NameOf(TagSuggestionDto s) =>
        s.TagId is { } id ? Directory.Find(id)?.Name ?? s.RequestedName ?? $"#{id}" : s.RequestedName ?? "?";

    /// <summary>
    /// Ordinary tags the mod doesn't have, doesn't count as through its tags, and nobody suggested yet (voting on that one
    /// is the way then).
    /// </summary>
    private bool CanSuggest(TagDto tag)
    {
        if (tag.IsType || data is null || data.Items.Any(s => s.TagId == tag.Id))
            return false;
        return TagIds.All(own => !Directory.Graph.SelfAndAncestors(own).Contains(tag.Id));
    }

    private async Task SuggestAsync()
    {
        sending = true;
        error = null;
        try
        {
            var request = new TagSuggestRequest
            {
                TagId = requesting ? null : pickedTag.FirstOrDefault(),
                NewTagName = requesting ? newTagName.Trim() : null,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            };
            await Api.Suggest(GroupId, request);
            CloseForm();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
        }
        finally
        {
            sending = false;
        }
    }

    private void CloseForm()
    {
        suggesting = requesting = false;
        pickedTag = [];
        newTagName = reason = string.Empty;
    }

    private Task VoteAsync(TagSuggestionDto s, int value) => RunAsync(s, () => Api.Vote(s.Id, new TagVoteRequest { Value = value }));

    private async Task ApproveAsync(TagSuggestionDto s)
    {
        if (await RunAsync(s, () => Api.Approve(s.Id)) && s.TagId is { } tagId)
            await TagApproved.InvokeAsync(tagId);
    }

    private Task RejectAsync(TagSuggestionDto s) => RunAsync(s, () => Api.Reject(s.Id));

    /// <summary>Runs an action on one suggestion and loads the list again; false when it failed.</summary>
    private async Task<bool> RunAsync(TagSuggestionDto s, Func<Task<TagSuggestionDto>> action)
    {
        busyId = s.Id;
        error = null;
        try
        {
            await action();
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
            return false;
        }
        finally
        {
            busyId = null;
        }
    }

    public void Dispose() => Directory.Changed -= OnTagsChanged;
}
