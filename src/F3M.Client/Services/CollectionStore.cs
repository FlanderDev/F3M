using System.Text.Json;
using F3M.Shared.Helpers;
using Microsoft.JSInterop;

namespace F3M.Client.Services;

public sealed record CollectionItem(int GroupId, string Name);

/// <summary>What is saved in the browser's local storage for the collection builder.</summary>
public sealed record StoredCollection(string? Name, List<CollectionItem> Items);

/// <summary>
/// The collection being built on the website (plan 10.2). It lives in this browser's local storage and is
/// never sent to the server. Changes raise <see cref="Changed"/> so badges and buttons stay in step.
/// </summary>
public sealed class CollectionStore(IJSRuntime js)
{
    private const string StorageKey = "f3m.collection.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<CollectionItem> _items = [];
    private bool _loaded;

    public event Action? Changed;

    public IReadOnlyList<CollectionItem> Items => _items;

    public string? Name { get; private set; }

    public bool IsFull => _items.Count >= CollectionLink.MaxMods;

    /// <summary>Reads the saved collection once. Unreadable or blocked storage just means an empty collection.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var raw = await js.InvokeAsync<string?>("f3m.getItem", StorageKey);
            if (string.IsNullOrEmpty(raw)) return;

            var saved = JsonSerializer.Deserialize<StoredCollection>(raw, Json);
            if (saved is null) return;

            Name = saved.Name;
            _items.AddRange(saved.Items
                .Where(i => i.GroupId > 0)
                .DistinctBy(i => i.GroupId)
                .Take(CollectionLink.MaxMods));
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Corrupt or blocked storage: start with an empty collection.
        }
    }

    public bool Contains(int groupId) => _items.Any(i => i.GroupId == groupId);

    public async Task<bool> AddAsync(int groupId, string name)
    {
        if (Contains(groupId) || IsFull) return false;
        _items.Add(new CollectionItem(groupId, name));
        await SaveAsync();
        return true;
    }

    public Task RemoveAsync(int groupId)
    {
        _items.RemoveAll(i => i.GroupId == groupId);
        return SaveAsync();
    }

    public Task MoveAsync(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= _items.Count || target < 0 || target >= _items.Count) return Task.CompletedTask;
        (_items[index], _items[target]) = (_items[target], _items[index]);
        return SaveAsync();
    }

    public Task RenameAsync(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmed is not null && trimmed.Length > CollectionLink.MaxNameLength)
            trimmed = trimmed[..CollectionLink.MaxNameLength];
        Name = trimmed;
        return SaveAsync();
    }

    public Task ClearAsync()
    {
        _items.Clear();
        Name = null;
        return SaveAsync();
    }

    /// <summary>The link for the current collection, or null when it is empty or invalid.</summary>
    public CollectionLink? ToLink()
    {
        if (_items.Count == 0) return null;
        var ids = string.Join(',', _items.Select(i => i.GroupId));
        return CollectionLink.TryParse(ids, Name, out var link, out _) ? link : null;
    }

    private async Task SaveAsync()
    {
        Changed?.Invoke();
        try
        {
            var json = JsonSerializer.Serialize(new StoredCollection(Name, _items.ToList()), Json);
            await js.InvokeVoidAsync("f3m.setItem", StorageKey, json);
        }
        catch (Exception)
        {
            // Storage refused the write: the collection still works until the page is closed.
        }
    }
}
