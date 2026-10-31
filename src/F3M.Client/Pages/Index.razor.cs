using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace F3M.Client.Pages;

/// <summary>
/// Browse page with infinite scroll: the first batch is twice what fits on the screen, and the next batch of the same
/// size loads whenever the end of the list comes near the screen.
/// </summary>
public partial class Index
{
    private string searchTerm = string.Empty;
    private SortBy sortBy = SortBy.Newest;

    // The tag filter: a mod must have each of these tags, or a tag it includes. Kept in the address (?tags=a,b), so a
    // filtered list can be shared and the tag chips on the cards can link to it.
    private List<int> selectedTags = [];

    [SupplyParameterFromQuery(Name = "tags")]
    public string? TagsQuery { get; set; }

    // The first batch is loaded after the first render (it needs the screen size); until then a changed address only
    // sets the filter.
    private bool firstLoadStarted;
    private System.Timers.Timer? _debounce;

    private static readonly TimeSpan SearchSettleTime = TimeSpan.FromSeconds(2.5);
    private string? lastReportedSearch;

    // The mods shown so far, page by page; every page has batchSize mods, so page numbers stay consistent.
    private readonly List<Mod> mods = [];
    private int totalCount;
    private int nextPage = 1;
    private bool loading = true;
    private bool loadingMore;

    // Twice a screenful of cards, measured on the placeholders before the first load. The server sends at most 100.
    private const int DefaultBatchSize = 24;
    private const int MaxBatchSize = 100;
    private int batchSize = DefaultBatchSize;

    // Every new search starts a new generation; a batch that arrives for an older one is dropped.
    private int generation;

    // Start loading the next batch while the end of the list is still this far below the screen.
    private const int LoadAheadPixels = 800;

    private ElementReference placeholderGrid;
    private ElementReference listEnd;
    private DotNetObjectReference<Index>? selfReference;
    private IJSObjectReference? listEndWatcher;

    protected override async Task OnParametersSetAsync()
    {
        await Tags.LoadAsync();

        // Names that are no tag (any more) are dropped.
        List<int> fromAddress = [.. (TagsQuery ?? string.Empty)
            .Split(TagRules.ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Tags.Find).OfType<TagDto>().Select(t => t.Id).Distinct()];
        if (fromAddress.SequenceEqual(selectedTags))
            return;

        selectedTags = fromAddress;
        if (firstLoadStarted)
            await LoadMods();
    }

    private async Task OnTagsPicked(List<int> tags)
    {
        selectedTags = tags;
        // The address follows; OnParametersSetAsync then finds nothing changed.
        Navigation.NavigateTo(BrowseAddress(), replace: true);
        await LoadMods();
    }

    private string BrowseAddress()
    {
        var names = SelectedTagNames();
        return Navigation.GetUriWithQueryParameter("tags", names.Count > 0 ? string.Join(TagRules.ListSeparator, names) : null);
    }

    private List<string> SelectedTagNames() => [.. selectedTags.Select(Tags.Find).OfType<TagDto>().Select(t => t.Name)];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        try
        {
            var screenful = await JS.InvokeAsync<int>("f3m.gridScreenful", placeholderGrid);
            if (screenful > 0)
                batchSize = Math.Clamp(screenful * 2, 1, MaxBatchSize);
        }
        catch (JSException)
        {
            // Measuring failed; the default batch size works too.
        }

        firstLoadStarted = true;
        await LoadMods();

        try
        {
            selfReference = DotNetObjectReference.Create(this);
            listEndWatcher = await JS.InvokeAsync<IJSObjectReference>("f3m.watchListEnd", listEnd, selfReference, LoadAheadPixels);
            await listEndWatcher.InvokeVoidAsync("check");
        }
        catch (JSException)
        {
            // Without the watcher (an outdated f3m.js) the "Load more" button still works.
            listEndWatcher = null;
        }
    }

    private async Task LoadMoreClicked() => await LoadMore();

    /// <summary>Shows the first batch for the current search, filters and sort order, replacing what was shown.</summary>
    private async Task LoadMods()
    {
        var current = ++generation;
        var filtered = searchTerm.Trim().Length > 0 || selectedTags.Count > 0;
        _ = FindRequiredModAsync();
        loading = true;
        loadingMore = false;
        StateHasChanged();

        ModListResult page;
        try
        {
            page = await FetchPage(1);
        }
        catch
        {
            page = new ModListResult();
        }

        if (current != generation)
            return;

        mods.Clear();
        mods.AddRange(page.Items);
        totalCount = page.TotalCount;
        shownFiltered = filtered;
        if (!filtered)
            allCount = totalCount;
        nextPage = 2;
        loading = false;
        StateHasChanged();

        if (searchTerm.Trim().Length >= 3 && selectedTags.Count == 0)
            _ = ReportSearchLaterAsync(current, searchTerm.Trim(), totalCount);

        // The new list may end on the screen already (few results, or the page was scrolled down).
        if (listEndWatcher is not null)
            await listEndWatcher.InvokeVoidAsync("check");
    }

    /// <summary>
    /// Called by the infinite-scroll watcher when the end of the list is near the screen. Appends the next batch;
    /// false when there is nothing (more) to load right now.
    /// </summary>
    [JSInvokable]
    public async Task<bool> LoadMore()
    {
        if (loading || loadingMore || mods.Count >= totalCount)
            return false;

        var current = generation;
        loadingMore = true;
        StateHasChanged();

        try
        {
            var page = await FetchPage(nextPage);
            if (current != generation)
                return false;

            // A mod uploaded meanwhile shifts the pages by one; don't show anything twice.
            var shown = mods.Select(m => m.Id).ToHashSet();
            mods.AddRange(page.Items.Where(m => shown.Add(m.Id)));
            totalCount = page.TotalCount;
            nextPage++;
            return page.Items.Count > 0 && mods.Count < totalCount;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (current == generation)
            {
                loadingMore = false;
                StateHasChanged();
            }
        }
    }

    private Task<ModListResult> FetchPage(int page)
    {
        var names = SelectedTagNames();
        var tags = names.Count > 0 ? string.Join(TagRules.ListSeparator, names) : null;
        return ModsApi.GetMods(page, batchSize, searchTerm, sortBy, tags);
    }

    /// <summary>
    /// Counts a search for the statistics page once its results have stayed on screen for a moment, so the words typed
    /// on the way ("tex", "text", ...) don't count. Searches within a type or tag are left out: "no results" there
    /// says little about what the site is missing.
    /// </summary>
    private async Task ReportSearchLaterAsync(int current, string term, int results)
    {
        await Task.Delay(SearchSettleTime);
        if (current != generation || string.Equals(term, lastReportedSearch, StringComparison.OrdinalIgnoreCase))
            return;

        lastReportedSearch = term;
        try
        {
            await Telemetry.ReportSearch(new SearchReport { Term = term, Results = results });
        }
        catch
        {
            // Only statistics.
        }
    }

    private void OnSearchKeyUp(KeyboardEventArgs _)
    {
        // 1-2 characters are too short to search for, but an emptied box has to bring the full list back.
        if (searchTerm.Length is > 0 and < 3)
            return;

        _debounce?.Dispose();
        _debounce = new System.Timers.Timer(500);
        _debounce.Elapsed += async (_, _) =>
        {
            _debounce?.Dispose();
            await InvokeAsync(LoadMods);
        };
        _debounce.AutoReset = false;
        _debounce.Start();
    }

    private static string SortText(SortBy sort) => sort switch
    {
        SortBy.Newest => "Newest",
        SortBy.Oldest => "Oldest",
        SortBy.DownloadsDesc => "Most downloads",
        SortBy.DownloadsAsc => "Fewest downloads",
        SortBy.NameAsc => "Name A–Z",
        SortBy.NameDesc => "Name Z–A",
        _ => sort.ToString(),
    };

    /// <summary>A type chip: that type instead of any other picked; null for all types. The other tags stay.</summary>
    private Task SelectType(int? typeTagId) =>
        OnTagsPicked([.. selectedTags.Where(id => !Tags.IsType(id)), .. typeTagId is { } id ? [id] : Array.Empty<int>()]);

    private bool HasFilters => searchTerm.Length > 0 || selectedTags.Count > 0 || sortBy != SortBy.Newest;

    // How many mods there are without filters, from the last load that had none; null until then.
    private int? allCount;

    // Whether the list shown was filtered (by text or tags); the box may hold text not searched for yet.
    private bool shownFiltered;

    // After the bold number: " of 48 mods match", " mods match", " mods to choose from".
    private string CountText
    {
        get
        {
            if (shownFiltered && allCount is { } all && all != totalCount)
                return $" of {all} mods match";
            var noun = totalCount == 1 ? "mod" : "mods";
            return shownFiltered ? $" {noun} {(totalCount == 1 ? "matches" : "match")}" : $" {noun} to choose from";
        }
    }

    private static string TypeIcon(TagDto type) => type.TypeId switch
    {
        ModTemplates.BepInExPlugin => "bi-plug",
        ModTemplates.MissionsV1 or ModTemplates.MissionsV2 => "bi-flag",
        ModTemplates.Cosplay => "bi-person-bounding-box",
        ModTemplates.TexturePack => "bi-palette",
        _ => "bi-box"
    };

    /// <summary>The tag box holds the ordinary tags; the type pills the type tags.</summary>
    private Task OnOrdinaryTagsPicked(List<int> tags) => OnTagsPicked([.. tags, .. selectedTags.Where(Tags.IsType)]);

    // The mods the types need (a framework, a loader), by name; null when this site doesn't have it.
    private readonly Dictionary<string, Mod?> requiredMods = new(StringComparer.OrdinalIgnoreCase);

    private Mod? RequiredModOf(ModTemplate type) =>
        type.RequiredModName is { } name ? requiredMods.GetValueOrDefault(name) : null;

    /// <summary>Looks up the mod the picked type needs, once per type, for the link next to its description.</summary>
    private async Task FindRequiredModAsync()
    {
        var type = selectedTags.Select(id => ModTemplates.Find(Tags.Find(id)?.TypeId)).OfType<ModTemplate>().FirstOrDefault();
        if (type?.RequiredModName is not { } name || requiredMods.ContainsKey(name))
            return;

        try
        {
            var found = await ModsApi.SearchMods(name);
            requiredMods[name] = found.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            StateHasChanged();
        }
        catch
        {
            // Just no link.
        }
    }

    private async Task ClearSearch()
    {
        searchTerm = string.Empty;
        _debounce?.Dispose();
        await LoadMods();
    }

    private async Task ClearFilters()
    {
        searchTerm = string.Empty;
        sortBy = SortBy.Newest;
        if (selectedTags.Count > 0)
        {
            selectedTags = [];
            Navigation.NavigateTo(BrowseAddress(), replace: true);
        }
        await LoadMods();
    }

    public async ValueTask DisposeAsync()
    {
        _debounce?.Dispose();
        if (listEndWatcher is not null)
        {
            try
            {
                await listEndWatcher.InvokeVoidAsync("dispose");
                await listEndWatcher.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is gone already.
            }
        }

        selfReference?.Dispose();
    }
}
