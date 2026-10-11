using F3M.Shared;
using F3M.Shared.Api;
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
    private const string CategoryAll = "All Categories";
    private string selectedCategory = CategoryAll;
    private string[] categories = [];
    private string searchTerm = string.Empty;
    private SortBy sortBy = SortBy.Newest;
    private System.Timers.Timer? _debounce;

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

    protected override async Task OnInitializedAsync()
    {
        categories = [.. await ModsApi.GetCategories()];
    }

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
        nextPage = 2;
        loading = false;
        StateHasChanged();

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
        var specificCategory = selectedCategory == CategoryAll ? null : selectedCategory;
        return ModsApi.GetMods(page, batchSize, searchTerm, specificCategory, sortBy);
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

    private async Task SelectCategory(string cat) { selectedCategory = cat; await LoadMods(); }
    private async Task ClearFilters() { searchTerm = string.Empty; selectedCategory = CategoryAll; sortBy = SortBy.Newest; await LoadMods(); }

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
