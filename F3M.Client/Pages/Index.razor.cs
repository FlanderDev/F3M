using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Web;
using System.Net.Http.Json;

namespace F3M.Client.Pages;

public partial class Index
{
    private ModListResult? result;
    private List<string> categories = [];
    private bool loading = true;
    private string searchTerm = string.Empty;
    private string selectedCategory = "All";
    private string sortBy = "newest";
    private int currentPage = 1;
    private const int pageSize = 18;
    private System.Timers.Timer? _debounce;

    protected override async Task OnInitializedAsync()
    {
        await LoadCategories();
        await LoadMods();
    }

    private async Task LoadCategories()
        => categories = await Http.GetFromJsonAsync<List<string>>("api/mods/categories") ?? [];

    private async Task LoadMods()
    {
        loading = true;
        StateHasChanged();
        try
        {
            var url = $"api/mods?page={currentPage}&pageSize={pageSize}" +
                      $"&search={Uri.EscapeDataString(searchTerm)}" +
                      $"&category={Uri.EscapeDataString(selectedCategory)}&sort={sortBy}";
            result = await Http.GetFromJsonAsync<ModListResult>(url);
        }
        catch { result = new ModListResult(); }
        finally { loading = false; }
    }

    private void OnSearchKeyUp(KeyboardEventArgs _)
    {
        _debounce?.Dispose();
        _debounce = new System.Timers.Timer(500);
        _debounce.Elapsed += async (_, _) =>
        {
            _debounce?.Dispose();
            currentPage = 1;
            await InvokeAsync(LoadMods);
        };
        _debounce.AutoReset = false;
        _debounce.Start();
    }

    private async Task SelectCategory(string cat) { selectedCategory = cat; currentPage = 1; await LoadMods(); }
    private async Task GoToPage(int p) { currentPage = p; await LoadMods(); }
    private async Task ClearFilters() { searchTerm = string.Empty; selectedCategory = "All"; sortBy = "newest"; currentPage = 1; await LoadMods(); }
}