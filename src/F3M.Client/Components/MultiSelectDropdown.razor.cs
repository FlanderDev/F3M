using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components;

public partial class MultiSelectDropdown<TModel>
{
    [Parameter, EditorRequired]
    public Func<HttpClient, string, Task<List<TModel>?>> LoadValuesAsync { get; set; }

    /// <summary>Items currently selected (two-way bindable).</summary>
    [Parameter]
    public List<InternalItem> SelectedItems { get; set; } = [];

    [Parameter]
    public EventCallback<List<InternalItem>> SelectedItemsChanged { get; set; }

    /// <summary>Called when the dropdown closes. Receives the current selection.</summary>
    /// 
    [Parameter]
    public EventCallback<List<InternalItem>> OnDropdownClosed { get; set; }

    /// <summary>Minimum characters before the backend is queried.</summary>
    /// 
    [Parameter]
    public int MinSearchLength { get; set; } = 3;

    /// <summary>Debounce in milliseconds between keystrokes and the HTTP call.</summary>
    /// 
    [Parameter]
    public int DebounceMs { get; set; } = 500;

    /// <summary>Backend endpoint. "{query}" is replaced with the search term.</summary>
    /// 
    [Parameter]
    public string SearchEndpoint { get; set; } = R.Mods.Base;

    [Parameter]
    public string Placeholder { get; set; } = "Select items…";

    [Parameter]
    public string SearchPlaceholder { get; set; } = "Search…";

    /* ─── Internal state ─── */
    private bool _isOpen;
    private bool _isLoading;
    private string _searchText = string.Empty;
    private List<InternalItem> _items = [];
    private System.Timers.Timer? _debounce;

    /* ─── Open / close ─── */
    private async Task ToggleDropdown()
    {
        if (_isOpen)
            await CloseDropdown();
        else
            _isOpen = true;
    }

    private async Task CloseDropdown()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _searchText = string.Empty;
        _items.Clear();

        if (OnDropdownClosed.HasDelegate)
            await OnDropdownClosed.InvokeAsync(SelectedItems);
    }

    /* ─── Search / debounce ─── */
    private void OnSearchInput(ChangeEventArgs e)
    {
        _searchText = e.Value?.ToString() ?? string.Empty;

        _debounce?.Dispose();

        if (_searchText.Length < MinSearchLength)
        {
            _items.Clear();
            StateHasChanged();
            return;
        }

        _debounce = new(DebounceMs);
        _debounce.Elapsed += async (_, _) =>
        {
            _debounce?.Dispose();
            await InvokeAsync(LoadItemsAsync);
        };
        _debounce.AutoReset = false;
        _debounce.Start();
    }

    private async Task LoadItemsAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            var url = SearchEndpoint.Replace("{query}", Uri.EscapeDataString(_searchText));
            //var result = await Http.GetFromJsonAsync<ModListResult>(url);
            var result = await LoadValuesAsync(Http, url);
            if (result == null)
            {
                return;
            }

            _items = [.. result.Select(s => new InternalItem(s, false))];
        }
        catch
        {
            _items = [];
            // Optionally surface the error via a parameter callback.
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    /* ─── Selection ─── */
    private async Task ToggleItem(InternalItem item)
    {
        var existing = SelectedItems.FirstOrDefault(s => s == item);
        if (existing is not null)
            SelectedItems.Remove(existing);
        else
            SelectedItems.Add(item);

        await SelectedItemsChanged.InvokeAsync(SelectedItems);
    }

    private async Task RemoveItem(InternalItem item)
    {
        SelectedItems.RemoveAll(s => s == item);
        await SelectedItemsChanged.InvokeAsync(SelectedItems);
    }

    /* ─── Model ─── */
    public record InternalItem(TModel Model, bool IsChecked);
}