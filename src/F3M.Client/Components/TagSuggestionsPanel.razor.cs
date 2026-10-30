using F3M.Client.Shared;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace F3M.Client.Components;

public partial class TagSuggestionsPanel
{
    [Parameter, EditorRequired] public int GroupId { get; set; }

    /// <summary>The mod's tags: they, and the tags they count as, can't be suggested.</summary>
    [Parameter, EditorRequired] public IReadOnlyCollection<int> TagIds { get; set; } = [];

    /// <summary>A suggestion was approved: the tag is the mod's now (replacing the tags it's a kind of).</summary>
    [Parameter] public EventCallback<int> TagApproved { get; set; }

    /// <summary>The admin queue, where requests for new tags are turned into tags.</summary>
    private static string SuggestionQueueUrl => $"{Paths.AdminTags}?view=suggestions";

    // Chips on the page; the rest is in the dialog.
    private const int SummaryCount = 8;

    // Few enough to show the whole tree unfolded.
    private const int UnfoldAllUpTo = 30;

    // New tags shown before "Show all".
    private const int ProposalCount = 5;

    private enum ComposeMode { None, Suggest, Create }

    private enum TagStatus { Open, Refines, OnMod, Implied, Suggested, Rejected }

    /// <summary>What a tag is to this mod. <c>Via</c> names the mod's tag it's implied by or refines.</summary>
    private sealed record Status(TagStatus Kind, string? Via = null, TagSuggestionDto? Suggestion = null);

    private ModTagSuggestions? data;
    private List<TagProposalDto> proposals = [];
    private int? busyProposal;
    private bool showAllProposals;
    private string? loadError;
    private string? error;
    private int? busyId;

    private bool dialogOpen;
    private bool focusDialog;
    private bool revealPending;
    private ElementReference dialog;
    private string search = string.Empty;
    private readonly HashSet<int> expanded = [];

    private ComposeMode mode;
    private int? selectedTagId;
    private int? createParentId;
    private string newTagName = string.Empty;
    private string newTagDescription = string.Empty;
    private string reason = string.Empty;
    private bool sending;

    private bool OutOfSuggestions => data?.RemainingToday == 0;

    private bool SignedIn => data is { CanVote: true } or { CanDecide: true };

    private TagProposalDto? ProposalOf(int tagId) => proposals.FirstOrDefault(p => p.TagId == tagId);

    private IEnumerable<int> Branches => Directory.Tags.Where(t => Directory.Graph.ChildrenOf(t.Id).Count > 0).Select(t => t.Id);

    private bool AllExpanded => Branches.All(expanded.Contains);

    protected override async Task OnInitializedAsync()
    {
        Directory.Changed += OnTagsChanged;
        await Directory.LoadAsync();
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!dialogOpen)
            return;
        try
        {
            if (focusDialog)
            {
                focusDialog = false;
                await dialog.FocusAsync();
            }
            if (revealPending)
            {
                revealPending = false;
                await JS.InvokeVoidAsync("f3m.revealSelected", dialog, ".f3m-tree-row.selected, .f3m-tagdlg-item.selected");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException)
        {
            // Closed again already, or an outdated f3m.js: nothing scrolls.
        }
    }

    private void OnTagsChanged() => InvokeAsync(StateHasChanged);

    private async Task LoadAsync()
    {
        try
        {
            var loading = Api.GetForMod(GroupId);
            proposals = await Api.GetProposals();
            data = await loading;
            loadError = null;
        }
        catch (Exception ex)
        {
            loadError = $"The suggestions could not be loaded: {ApiErrors.Describe(ex)}";
        }
    }

    // ── The dialog ──────────────────────────────────────────────────────────

    private void OpenDialog(int? tagId = null)
    {
        if (!dialogOpen)
        {
            // Open the branches down to the mod's tags and the suggested ones; small trees open completely.
            expanded.Clear();
            var shown = Directory.Tags.Count <= UnfoldAllUpTo
                ? Branches
                : TagIds.Concat(data?.Items.Select(s => s.TagId).OfType<int>() ?? [])
                    .SelectMany(Directory.Graph.SelfAndAncestors);
            expanded.UnionWith(shown);
            error = null;
        }

        dialogOpen = focusDialog = true;
        if (tagId is { } id)
            Reveal(id);
    }

    private void CloseDialog()
    {
        dialogOpen = false;
        search = string.Empty;
        ResetCompose();
    }

    private void OnDialogKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            CloseDialog();
    }

    private void ToggleAll()
    {
        if (AllExpanded)
            expanded.Clear();
        else
            expanded.UnionWith(Branches);
    }

    /// <summary>Unfolds the tree down to the tag and marks it.</summary>
    private void Reveal(int tagId)
    {
        search = string.Empty;
        Unfold(tagId);
        if (mode == ComposeMode.Suggest && StatusOf(Directory.Find(tagId)!) is not { Kind: TagStatus.Open or TagStatus.Refines })
            ClearCompose(keepSelection: true);
    }

    private void Unfold(int tagId)
    {
        expanded.UnionWith(Directory.Graph.SelfAndAncestors(tagId).Where(id => id != tagId));
        selectedTagId = tagId;
        revealPending = true;
    }

    // ── What a tag is to this mod ───────────────────────────────────────────

    private Status StatusOf(TagDto tag)
    {
        if (TagIds.Contains(tag.Id))
            return new(TagStatus.OnMod);
        if (data?.Items.FirstOrDefault(s => s.TagId == tag.Id) is { } suggestion)
            return new(TagStatus.Suggested, Suggestion: suggestion);
        if (data?.RejectedTagIds.Contains(tag.Id) == true)
            return new(TagStatus.Rejected);

        var ancestors = Directory.Graph.SelfAndAncestors(tag.Id);
        foreach (var own in TagIds)
        {
            if (Directory.Graph.SelfAndAncestors(own).Contains(tag.Id))
                return new(TagStatus.Implied, Directory.Find(own)?.Name);
        }
        foreach (var own in TagIds)
        {
            if (ancestors.Contains(own) && Directory.Find(own) is { IsType: false } refined)
                return new(TagStatus.Refines, refined.Name);
        }
        return new(TagStatus.Open);
    }

    private string? RowClass(TagDto tag) => StatusOf(tag).Kind switch
    {
        TagStatus.OnMod => "on-mod",
        TagStatus.Implied or TagStatus.Rejected => "muted",
        TagStatus.Suggested => "suggested",
        _ => null
    };

    /// <summary>The way down to the tag through its first parents: "Clothing › Lingerie". Empty for a top-level tag.</summary>
    private string PathOf(TagDto tag)
    {
        var names = new List<string>();
        var current = tag;
        while (current.ParentIds.Count > 0 && names.Count < TagRules.MaxDepth
               && current.ParentIds.Select(Directory.Find).OfType<TagDto>().MinBy(p => p.Name, StringComparer.OrdinalIgnoreCase) is { } parent)
        {
            names.Insert(0, parent.Name);
            current = parent;
        }
        return names.Count == 0 ? string.Empty : $"in {string.Join(" › ", names)}";
    }

    private TagDto? ExistingNamed(string name) => TagRules.Clean(name).Length > 0 ? Directory.Find(name) : null;

    private string NameOf(TagSuggestionDto s) =>
        s.TagId is { } id ? Directory.Find(id)?.Name ?? s.RequestedName ?? $"#{id}" : s.RequestedName ?? "?";

    private static string Signed(int score) => score > 0 ? $"+{score}" : score.ToString();

    private static string ChipClass(TagSuggestionDto s) =>
        s.IsRequest ? "f3m-tag-request" : s.Counts ? "f3m-tag-community" : string.Empty;

    private string ChipTitle(TagSuggestionDto s) =>
        s.IsRequest ? "A new tag visitors asked for"
        : s.Counts ? "Counts in searches"
        : $"Counts in searches from {data?.Threshold} points";

    // ── Suggesting ──────────────────────────────────────────────────────────

    /// <summary>A click on a tag in the tree: suggest it when it can be, otherwise just mark it.</summary>
    private void Pick(TagDto tag)
    {
        Unfold(tag.Id);
        if (data?.CanVote == true && !OutOfSuggestions && StatusOf(tag).Kind is TagStatus.Open or TagStatus.Refines)
        {
            mode = ComposeMode.Suggest;
            error = null;
        }
        else
        {
            if (mode == ComposeMode.Suggest)
                ClearCompose(keepSelection: true);
            // Nothing to suggest here: a click on a branch folds it open or shut.
            if (Directory.Graph.ChildrenOf(tag.Id).Count > 0 && !expanded.Remove(tag.Id))
                expanded.Add(tag.Id);
        }
    }

    private void StartCreate(int? parentId)
    {
        mode = ComposeMode.Create;
        createParentId = parentId;
        selectedTagId = parentId;
        error = null;
    }

    private void ResetCompose() => ClearCompose(keepSelection: false);

    private void ClearCompose(bool keepSelection)
    {
        mode = ComposeMode.None;
        if (!keepSelection)
            selectedTagId = null;
        createParentId = null;
        newTagName = newTagDescription = reason = string.Empty;
    }

    private async Task SuggestAsync()
    {
        sending = true;
        error = null;
        try
        {
            var request = new TagSuggestRequest
            {
                TagId = mode == ComposeMode.Suggest ? selectedTagId : null,
                NewTagName = mode == ComposeMode.Create ? newTagName.Trim() : null,
                ParentTagId = mode == ComposeMode.Create ? createParentId : null,
                NewTagDescription = mode == ComposeMode.Create && !string.IsNullOrWhiteSpace(newTagDescription) ? newTagDescription.Trim() : null,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            };
            var created = await Api.Suggest(GroupId, request);
            var newTag = mode == ComposeMode.Create;
            ResetCompose();
            if (newTag)
                await Directory.ReloadAsync();
            await LoadAsync();
            if (created.TagId is { } id)
                Reveal(id);
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

    // ── Voting and deciding ─────────────────────────────────────────────────

    private async Task VoteProposalAsync(TagProposalDto p, int value)
    {
        busyProposal = p.TagId;
        error = null;
        try
        {
            var result = await Api.VoteProposal(p.TagId, new TagVoteRequest { Value = value });
            proposals = await Api.GetProposals();
            // Enough points: a regular tag now.
            if (!result.IsProposed)
                await Directory.ReloadAsync();
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
        }
        finally
        {
            busyProposal = null;
        }
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
