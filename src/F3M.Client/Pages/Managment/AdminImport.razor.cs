using F3M.Shared.Api;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using System.Text;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

public partial class AdminImport : IDisposable
{
    /// <summary>How often the page asks for progress while a step runs.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private const long MaxPlanSize = 5 * 1024 * 1024;

    private ImportStatusDto? status;
    private string? loadError;
    private string? actionError;
    private string? planMessage;
    private bool busy;

    private string search = string.Empty;
    private ImportModState? stateFilter;
    private readonly HashSet<string> acceptedKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource disposed = new();
    private bool polling;

    private bool Running => status?.Job.Running == true;

    private int MissingFiles => status is null ? 0 : status.Plan.Files - status.Plan.FilesDownloaded;

    /// <summary>What the main import button imports: everything not on the site, except mods waiting for review.</summary>
    private List<string> KeysToImport => status?.Mods
        .Where(m => m.State is not (ImportModState.Imported or ImportModState.HeldBack))
        .Select(m => m.Key)
        .ToList() ?? [];

    private int ModsToImport => KeysToImport.Count;

    private List<ImportModDto> HeldBack => status?.Mods.Where(m => m.State == ImportModState.HeldBack).ToList() ?? [];

    private List<ImportModDto> Problems => status?.Mods
        .Where(m => m.State is ImportModState.DownloadFailed or ImportModState.Failed)
        .ToList() ?? [];

    private IEnumerable<ImportModDto> FilteredMods => (status?.Mods ?? [])
        .Where(m => stateFilter is null || m.State == stateFilter)
        .Where(m => string.IsNullOrWhiteSpace(search)
                    || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || m.Author.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || m.Key.Contains(search, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            status = await Api.GetStatusAsync(disposed.Token);
            loadError = null;
            acceptedKeys.IntersectWith(HeldBack.Select(m => m.Key));
        }
        catch (OperationCanceledException) when (disposed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            loadError = DescribeError(ex);
        }

        if (Running)
            _ = PollAsync();
    }

    /// <summary>Refreshes every second while a step runs, once more when it ends.</summary>
    private async Task PollAsync()
    {
        if (polling)
            return;
        polling = true;
        try
        {
            while (!disposed.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, disposed.Token);
                status = await Api.GetStatusAsync(disposed.Token);
                acceptedKeys.IntersectWith(HeldBack.Select(m => m.Key));
                StateHasChanged();
                if (!Running)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            loadError = DescribeError(ex);
            StateHasChanged();
        }
        finally
        {
            polling = false;
        }
    }

    private Task StartDownload() => RunAsync(() => Api.StartDownloadAsync());

    private Task StartImport()
    {
        var keys = KeysToImport;
        return RunAsync(() => Api.StartImportAsync(new ImportStartDto { OnlyKeys = keys }));
    }

    /// <summary>Imports the held-back mods the admin ticked, storing them despite their warnings.</summary>
    private Task ImportAccepted()
    {
        var keys = acceptedKeys.ToList();
        return RunAsync(() => Api.StartImportAsync(new ImportStartDto { OnlyKeys = keys, AcceptWarningsFor = keys }));
    }

    private Task Cancel() => RunAsync(async () =>
    {
        await Api.CancelAsync();
        return true;
    });

    private async Task RunAsync<T>(Func<Task<T>> action)
    {
        busy = true;
        actionError = null;
        planMessage = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            actionError = DescribeError(ex);
        }
        finally
        {
            busy = false;
        }

        await RefreshAsync();
    }

    private void ToggleAccepted(string key, bool accepted)
    {
        if (accepted)
            acceptedKeys.Add(key);
        else
            acceptedKeys.Remove(key);
    }

    private void SetAllAccepted(bool accepted)
    {
        acceptedKeys.Clear();
        if (accepted)
            acceptedKeys.UnionWith(HeldBack.Select(m => m.Key));
    }

    private async Task DownloadPlan()
    {
        actionError = null;
        try
        {
            var plan = await Api.GetPlanAsync();
            await JS.InvokeVoidAsync("f3m.downloadFile", "import-plan.json", Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Json)));
        }
        catch (Exception ex)
        {
            actionError = DescribeError(ex);
        }
    }

    private async Task ReplacePlan(InputFileChangeEventArgs e)
    {
        string json;
        try
        {
            await using var stream = e.File.OpenReadStream(MaxPlanSize);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            actionError = $"The file could not be read: {ex.Message}";
            return;
        }

        await RunAsync(async () =>
        {
            var info = await Api.ReplacePlanAsync(new ImportPlanJsonDto { Json = json });
            planMessage = $"Plan replaced: {info.Mods} mods, {info.Files} files.";
            return info;
        });
    }

    private Task ResetPlan() => RunAsync(async () =>
    {
        var info = await Api.ResetPlanAsync();
        planMessage = "Back to the plan shipped with the server.";
        return info;
    });

    private int Count(ImportModState state) => status?.Mods.Count(m => m.State == state) ?? 0;

    private static string Label(ImportModState state) => state switch
    {
        ImportModState.NotDownloaded => "Not downloaded",
        ImportModState.DownloadFailed => "Download failed",
        ImportModState.Downloaded => "Ready to import",
        ImportModState.PartlyImported => "Partly imported",
        ImportModState.Imported => "Imported",
        ImportModState.HeldBack => "Held back",
        ImportModState.Failed => "Import failed",
        _ => state.ToString(),
    };

    private static string BadgeClass(ImportModState state) => state switch
    {
        ImportModState.Imported => "bg-success",
        ImportModState.Downloaded => "bg-info text-dark",
        ImportModState.PartlyImported => "bg-primary",
        ImportModState.HeldBack => "bg-warning text-dark",
        ImportModState.DownloadFailed or ImportModState.Failed => "bg-danger",
        _ => "bg-secondary",
    };

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private static int Percent(int done, int total) => total == 0 ? 0 : (int)Math.Round(done * 100.0 / total);

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The server's message from a 400 (a JSON string), or the exception's own message.</summary>
    private static string DescribeError(Exception ex)
    {
        if (ex is not ApiException { ResponseBody: { Length: > 0 } body })
            return ex.Message;

        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();
    }
}
