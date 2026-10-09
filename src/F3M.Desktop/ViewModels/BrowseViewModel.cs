using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Shared.Models;

namespace F3M.Desktop.ViewModels;

/// <summary>One card in the Browse grid, built from index.json.</summary>
public sealed partial class ModCard : ObservableObject
{
    public ModCard(IndexGroup group, Action<ModCard> open)
    {
        Group = group;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public IndexGroup Group { get; }
    public int GroupId => Group.GroupId;
    public string Name => Group.Name;
    public string Author => Group.Author;
    public string Category => Group.Category;
    public string Version => Group.LatestVersion;
    public IRelayCommand OpenCommand { get; }

    [ObservableProperty]
    private Bitmap? _preview;

    /// <summary>"Deployed", "In profile", "Cached", or empty.</summary>
    [ObservableProperty]
    private string _state = string.Empty;
}

/// <summary>The detail panel, from the signed version document.</summary>
public sealed record ModDetail(
    string Name,
    string Version,
    string Author,
    string Category,
    string Description,
    string Size,
    string Dependencies,
    string Published,
    string? Unsupported);

public sealed partial class BrowseViewModel : ObservableObject
{
    public const string AllCategories = "All categories";

    private readonly AppServices _app;
    private readonly MainViewModel _shell;
    private List<ModCard> _all = [];

    public BrowseViewModel(AppServices app, MainViewModel shell)
    {
        _app = app;
        _shell = shell;
        _category = AllCategories;
        _shell.StateChanged += (_, _) => RefreshStates();
        Categories.Add(AllCategories);
    }

    public ObservableCollection<ModCard> Cards { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<string> Sorts { get; } = new() { "Newest", "Name" };

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string? _category;

    [ObservableProperty]
    private string _sort = "Newest";

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ModCard? _selected;

    [ObservableProperty]
    private ModDetail? _detail;

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnCategoryChanged(string? value) => ApplyFilter();

    partial void OnSortChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ModCard? value) => _ = LoadDetailAsync(value);

    /// <summary>Uses the catalog already loaded, and loads it when nothing is loaded yet.</summary>
    public async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            if (_app.Catalog.Index is null)
                await _app.Catalog.RefreshIndexAsync(CancellationToken.None);

            var groups = _app.Catalog.Index?.Groups ?? new List<IndexGroup>();
            _all = groups.Select(g => new ModCard(g, card => Selected = card)).ToList();

            var previous = Category;
            Categories.Clear();
            Categories.Add(AllCategories);
            foreach (var category in _all.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c))
                Categories.Add(category);
            Category = previous is not null && Categories.Contains(previous) ? previous : AllCategories;

            Status = _app.Catalog.IndexFromCache
                ? "Offline. Showing the last catalog that loaded."
                : _all.Count == 0 ? "No mods are published yet." : string.Empty;

            RefreshStates();
            ApplyFilter();
            _ = LoadPreviewsAsync(_all);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Opens the detail of one mod, for an f3m://v1/open link.</summary>
    public async Task SelectGroupAsync(int groupId)
    {
        if (_all.Count == 0) await ReloadAsync();
        var card = _all.FirstOrDefault(c => c.GroupId == groupId);
        if (card is null)
        {
            _shell.Notify($"Mod {groupId} is not in the catalog.");
            return;
        }

        Search = string.Empty;
        Category = AllCategories;
        ApplyFilter();
        Selected = card;
    }

    [RelayCommand]
    private Task RefreshCatalogAsync() => _shell.RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        if (Selected is not null) await _shell.AddToActiveProfileAsync(Selected.GroupId, Selected.Name);
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (Selected is not null) await _shell.DownloadModAsync(Selected.GroupId, null, Selected.Name);
    }

    private void RefreshStates()
    {
        var deployed = new HashSet<int>();
        try
        {
            deployed = _app.Deploy.LoadState().Mods.Select(m => m.GroupId).ToHashSet();
        }
        catch (UserException)
        {
            // An unreadable state file only hides the "Deployed" badge.
        }

        var inProfile = _shell.ActiveProfile?.GroupIds.ToHashSet() ?? new HashSet<int>();
        foreach (var card in _all)
        {
            card.State = deployed.Contains(card.GroupId) ? "Deployed"
                : inProfile.Contains(card.GroupId) ? "In profile"
                : _app.Downloads.IsCached(card.GroupId, card.Group.LatestVersionId) ? "Cached"
                : string.Empty;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ModCard> query = _all;
        if (!string.IsNullOrEmpty(Category) && Category != AllCategories)
            query = query.Where(c => c.Category == Category);

        var term = Search.Trim();
        if (term.Length > 0)
            query = query.Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                     || c.Author.Contains(term, StringComparison.OrdinalIgnoreCase));

        query = Sort == "Name"
            ? query.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : query.OrderByDescending(c => c.Group.UpdatedAt);

        Cards.Clear();
        foreach (var card in query) Cards.Add(card);
    }

    private async Task LoadPreviewsAsync(IEnumerable<ModCard> cards)
    {
        foreach (var card in cards)
        {
            if (string.IsNullOrEmpty(card.Group.PreviewUrl)) continue;
            try
            {
                var bytes = await _app.Catalog.FetchBytesAsync(card.Group.PreviewUrl, CancellationToken.None);
                if (bytes is null) continue;
                using var stream = new MemoryStream(bytes);
                card.Preview = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Info($"Preview for {card.Name} not shown: {ex.Message}");
            }
        }
    }

    private async Task LoadDetailAsync(ModCard? card)
    {
        Detail = null;
        if (card is null) return;

        try
        {
            var doc = await _app.Catalog.GetVersionAsync(card.GroupId, card.Group.LatestVersionId, CancellationToken.None);
            if (Selected != card) return;

            string? unsupported = null;
            try
            {
                PlacementRules.ValidateVersion(doc);
            }
            catch (UserException ex)
            {
                unsupported = ex.Message;
            }

            var dependencies = doc.Dependencies
                .Select(d => _app.Catalog.Group(d.GroupId)?.Name ?? $"Mod {d.GroupId}")
                .ToList();
            Detail = new ModDetail(
                doc.Name,
                doc.Version,
                doc.Author,
                doc.Category,
                doc.Description,
                FileOps.FormatBytes(doc.Files.Sum(f => f.Size)),
                dependencies.Count == 0 ? "None" : string.Join(", ", dependencies),
                doc.PublishedAt.ToString("yyyy-MM-dd"),
                unsupported);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Status = ex.Message;
        }
    }
}
