using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;

namespace F3M.Client.Components;

public partial class MultiSelectDropdown
{
    /* ─── Parameters ─── */

    /// <summary>Items currently selected (two-way bindable).</summary>
    [Parameter]
    public List<DropdownItem> SelectedItems { get; set; } = [];

    [Parameter]
    public EventCallback<List<DropdownItem>> SelectedItemsChanged { get; set; }

    /// <summary>Called when the dropdown closes. Receives the current selection.</summary>
    /// 
    [Parameter]
    public EventCallback<List<DropdownItem>> OnDropdownClosed { get; set; }

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
    private List<DropdownItem> _items = new();
    private System.Timers.Timer? _debounce;
    private ElementReference _wrapperRef;

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
            await InvokeAsync(FetchItemsAsync);
        };
        _debounce.AutoReset = false;
        _debounce.Start();
    }

    private async Task FetchItemsAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            var url = SearchEndpoint.Replace("{query}", Uri.EscapeDataString(_searchText));
            var result = await Http.GetFromJsonAsync<List<DropdownItem>>(url);
            _items = result ?? [];
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

    private async Task ToggleItem(DropdownItem item)
    {
        var existing = SelectedItems.FirstOrDefault(s => s.Value == item.Value);
        if (existing is not null)
            SelectedItems.Remove(existing);
        else
            SelectedItems.Add(item);

        await SelectedItemsChanged.InvokeAsync(SelectedItems);
    }

    private async Task RemoveItem(DropdownItem item)
    {
        SelectedItems.RemoveAll(s => s.Value == item.Value);
        await SelectedItemsChanged.InvokeAsync(SelectedItems);
    }

    /* ─── Click-outside (JS interop alternative: lightweight CSS approach) ─── */
    // For a full click-outside implementation inject IJSRuntime and call a small
    // JS helper. The panel closes via ToggleDropdown or the chip-remove button.

    public void Dispose() => _debounce?.Dispose();

    /* ─── Model ─── */

    public record DropdownItem(string Value, string Label);
}