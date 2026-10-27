using F3M.Client.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Pages.Managment;

public partial class TagSuggestionQueue
{
    /// <summary>Something was decided or created: the page refreshes the open count and the tags.</summary>
    [Parameter] public EventCallback Changed { get; set; }

    private static readonly (TagSuggestionState? Value, string Label)[] States =
    [
        (TagSuggestionState.Open, "Open"),
        (TagSuggestionState.Approved, "Approved"),
        (TagSuggestionState.Rejected, "Rejected"),
        (null, "All"),
    ];

    private TagSuggestionState? state = TagSuggestionState.Open;
    private List<TagSuggestionAdminDto> items = [];
    private TagSuggestionLimits limits = new();
    private bool loading = true;
    private bool busy;
    private string? error;
    private string? notice;
    private string search = string.Empty;

    // A request being turned into a tag.
    private int? creatingId;
    private TagEditDto? creating;

    private int CountingCount => items.Count(s => s.Counts);

    private IEnumerable<TagSuggestionAdminDto> Filtered
    {
        get
        {
            var typed = search.Trim();
            return typed.Length == 0
                ? items
                : items.Where(s => s.ModName.Contains(typed, StringComparison.OrdinalIgnoreCase)
                                   || NameOf(s).Contains(typed, StringComparison.OrdinalIgnoreCase));
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await Directory.LoadAsync();
        try
        {
            limits = await Api.GetLimits();
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        loading = true;
        try
        {
            items = await Api.GetQueue(state);
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
        }
        finally
        {
            loading = false;
        }
    }

    private async Task SelectStateAsync(TagSuggestionState? value)
    {
        state = value;
        creatingId = null;
        await LoadAsync();
    }

    private string NameOf(TagSuggestionDto s) =>
        s.TagId is { } id ? Directory.Find(id)?.Name ?? s.RequestedName ?? $"#{id}" : s.RequestedName ?? "?";

    private static string VotersTitle(TagSuggestionAdminDto s) =>
        $"For: {(s.UpVoters.Count > 0 ? string.Join(", ", s.UpVoters) : "nobody")}\nAgainst: {(s.DownVoters.Count > 0 ? string.Join(", ", s.DownVoters) : "nobody")}";

    private Task ApproveAsync(TagSuggestionAdminDto s) =>
        RunAsync(async () => { await Api.Approve(s.Id); return $"Approved {NameOf(s)} for {s.ModName}."; });

    private Task RejectAsync(TagSuggestionAdminDto s) =>
        RunAsync(async () => { await Api.Reject(s.Id); return $"Rejected {NameOf(s)} for {s.ModName}."; });

    private Task ApproveCountingAsync() =>
        RunAsync(async () =>
        {
            var count = await Api.ApproveCounting();
            var left = CountingCount - count;
            return $"Approved {count} suggestion{(count == 1 ? string.Empty : "s")}."
                   + (left > 0 ? $" {left} stayed open: their mods have the most tags they can have." : string.Empty);
        });

    private void StartCreate(TagSuggestionAdminDto s)
    {
        creatingId = s.Id;
        creating = new TagEditDto { Name = s.RequestedName ?? string.Empty };
        error = notice = null;
    }

    private Task CreateAsync()
    {
        if (creatingId is not { } id || creating is null)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await Api.CreateTag(id, creating);
            creatingId = null;
            await Directory.ReloadAsync();
            return $"Created the tag {creating.Name}; the requests for it are suggestions now.";
        });
    }

    private Task SaveLimitsAsync() =>
        RunAsync(async () =>
        {
            limits = await Api.SetLimits(limits);
            return $"Suggestions count from {limits.Threshold} points; each account can start {limits.DailyLimit} a day.";
        });

    /// <summary>Runs an action, shows its message, and loads the list again.</summary>
    private async Task RunAsync(Func<Task<string>> action)
    {
        busy = true;
        error = notice = null;
        try
        {
            notice = await action();
            await LoadAsync();
            await Changed.InvokeAsync();
        }
        catch (Exception ex)
        {
            error = ApiErrors.Describe(ex);
        }
        finally
        {
            busy = false;
        }
    }
}
