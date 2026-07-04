using F3M.Client.Business;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Web;
using System.Net.Http.Json;

namespace F3M.Client.Pages;

public partial class Index
{
    private const string CategoryAll = "All Categories";
    private string selectedCategory = CategoryAll;
    private ModListResult? result;
    private string[] categories = [];
    private bool loading = true;
    private string searchTerm = string.Empty;
    private Configuration.SortBy sortBy = Configuration.SortBy.Newest;
    private int currentPage = 1;
    private const int pageSize = 18;
    private System.Timers.Timer? _debounce;

    protected override async Task OnInitializedAsync()
    {
        categories = await Http.LoadCategoriesAsync();
        await LoadMods();
    }

    private async Task LoadMods()
    {
        loading = true;
        StateHasChanged();

        try
        {
            var specificCategory = selectedCategory == CategoryAll ? string.Empty : selectedCategory;
            var query = Endpoints.Mods.GetMods(currentPage, pageSize, searchTerm, specificCategory, sortBy);
            result = await Http.GetFromJsonAsync<ModListResult>(query);
        }
        catch { result = new ModListResult(); }
        finally { loading = false; }
    }

    private void OnSearchKeyUp(KeyboardEventArgs _)
    {
        if (searchTerm.Length < 3)
            return;

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
    private async Task ClearFilters() { searchTerm = string.Empty; selectedCategory = CategoryAll; sortBy = Configuration.SortBy.Newest; currentPage = 1; await LoadMods(); }
}