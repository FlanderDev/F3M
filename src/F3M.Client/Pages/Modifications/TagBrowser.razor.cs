using F3M.Client.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace F3M.Client.Pages.Modifications;

/// <summary>
/// The tags page: the whole tag tree to browse (each tag links to its mods), creating new tags without a mod, and the
/// votes on the tags users created.
/// </summary>
public partial class TagBrowser
{
    // Few enough to show the whole tree unfolded.
    private const int UnfoldAllUpTo = 30;

    private TagProposalInfo? info;
    private List<TagProposalDto> proposals = [];
    private bool loaded;
    private string? error;

    private ElementReference page;

    // What to scroll into view after the next render: the marked row in the tree, or the box about the tag.
    private string? revealPending;
    private string search = string.Empty;
    private readonly HashSet<int> expanded = [];
    private int? selectedTagId;

    private bool creating;
    private int? createParentId;
    private string newTagName = string.Empty;
    private string newTagDescription = string.Empty;
    private bool sending;
    private string? created;

    private bool CanCreate => info?.CanCreate == true;

    private bool OutOfTags => info?.RemainingToday == 0;

    private TagProposalDto? ProposalOf(int tagId) => proposals.FirstOrDefault(p => p.TagId == tagId);

    private IEnumerable<int> Branches => Directory.Tags.Where(t => Directory.Graph.ChildrenOf(t.Id).Count > 0).Select(t => t.Id);

    private bool AllExpanded => Branches.All(expanded.Contains);

    protected override async Task OnInitializedAsync()
    {
        Directory.Changed += OnTagsChanged;
        await Directory.LoadAsync();
        if (Directory.Tags.Count <= UnfoldAllUpTo)
            expanded.UnionWith(Branches);
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (revealPending is not { } selector)
            return;
        revealPending = null;
        try
        {
            await JS.InvokeVoidAsync("f3m.revealSelected", page, selector);
        }
        catch (JSException)
        {
            // An outdated f3m.js: nothing scrolls.
        }
    }

    private void OnTagsChanged() => InvokeAsync(StateHasChanged);

    private async Task LoadAsync()
    {
        try
        {
            var loading = Api.GetProposalInfo();
            proposals = await Api.GetProposals();
            info = await loading;
            error = null;
        }
        catch (Exception ex)
        {
            error = $"The new tags could not be loaded: {ApiErrors.Describe(ex)}";
        }
        finally
        {
            loaded = true;
        }
    }

    private void ToggleAll()
    {
        if (AllExpanded)
            expanded.Clear();
        else
            expanded.UnionWith(Branches);
    }

    /// <summary>A click on a tag: show what it is and unfold it. Another click on it folds it again.</summary>
    private void Pick(TagDto tag)
    {
        if (selectedTagId != tag.Id || !expanded.Remove(tag.Id))
            expanded.Add(tag.Id);
        Reveal(tag.Id);
        // On a phone the box is above the tree; on wider screens it's in view already.
        revealPending = ".f3m-tagpage-side .f3m-tagdlg-compose";
    }

    /// <summary>Unfolds the tree down to the tag and marks it.</summary>
    private void Reveal(int tagId)
    {
        search = string.Empty;
        expanded.UnionWith(Directory.Graph.SelfAndAncestors(tagId).Where(id => id != tagId));
        selectedTagId = tagId;
        revealPending = ".f3m-tree-row.selected";
    }

    private void StartCreate(int? parentId)
    {
        creating = true;
        createParentId = parentId;
        created = error = null;
        if (parentId is { } id)
            Reveal(id);
        revealPending = ".f3m-tagpage-side .f3m-tagdlg-compose";
    }

    private void CancelCreate()
    {
        creating = false;
        createParentId = null;
        newTagName = newTagDescription = string.Empty;
    }

    private async Task CreateAsync()
    {
        sending = true;
        error = null;
        try
        {
            var tag = await Api.Propose(new TagProposeRequest
            {
                Name = newTagName.Trim(),
                ParentTagId = createParentId,
                Description = string.IsNullOrWhiteSpace(newTagDescription) ? null : newTagDescription.Trim()
            });
            CancelCreate();
            created = tag.Name;
            await Directory.ReloadAsync();
            await LoadAsync();
            Reveal(tag.TagId);
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

    private static string Signed(int score) => score > 0 ? $"+{score}" : score.ToString();

    public void Dispose() => Directory.Changed -= OnTagsChanged;
}
