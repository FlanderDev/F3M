using FlanderDev.RouteGen.Abstractions;
using F3M.Shared.Models;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

public partial class ServerUpdateCard
{
    // Often while something happens, rarely otherwise; a restart takes about a minute.
    private static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);

    private ServerUpdateStatus? status;
    private bool loading = true;
    private bool requesting;
    private bool confirming;
    private bool restarting;
    private string? error;

    // The version this page was loaded with; when the server's changes, the update went through.
    private string? loadedCommit;
    private string? updatedTo;

    private readonly CancellationTokenSource stop = new();

    // Cut the current wait short, so a request is followed closely right away.
    private CancellationTokenSource? wake;

    /// <summary>An update is asked for or under way, or the server is gone for its restart.</summary>
    private bool Busy => restarting || status is { UpdateRequested: true } or { State: ServerUpdateState.Running };

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        loadedCommit = status?.RunningCommit;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using (wake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    try
                    {
                        await Task.Delay(Busy || status is { CheckRequested: true } ? BusyPoll : IdlePoll, wake.Token);
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        // Woken by a request.
                    }
                }
                wake = null;
                await RefreshAsync();
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var wasBusy = Busy;
            status = await Api.GetStatusAsync(stop.Token);
            restarting = false;
            if (wasBusy || error is not null)
                error = null;

            if (loadedCommit is not null && status.RunningCommit is { } running && !SameCommit(running, loadedCommit))
            {
                updatedTo = running;
                confirming = false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException) when (Busy)
        {
            // The container is being replaced; keep asking until the new one answers.
            restarting = true;
        }
        catch (ApiException ex) when (Busy && (int)ex.StatusCode >= 500)
        {
            restarting = true;
        }
        catch (Exception ex)
        {
            error = $"The update status could not be loaded: {Describe(ex)}";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task CheckNow()
    {
        error = null;
        requesting = true;
        try
        {
            status = await Api.RequestCheckAsync();
            wake?.Cancel();
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        finally
        {
            requesting = false;
        }
    }

    private async Task UpdateNow()
    {
        error = null;
        requesting = true;
        try
        {
            status = await Api.RequestUpdateAsync();
            confirming = false;
            wake?.Cancel();
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        finally
        {
            requesting = false;
        }
    }

    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    /// <summary>The running version may carry a shortened commit; compare by the shorter one.</summary>
    private static bool SameCommit(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        return length >= 7 && string.Equals(a[..length], b[..length], StringComparison.OrdinalIgnoreCase);
    }

    private static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} days ago";
    }

    /// <summary>The server's message for a refused request (in the response body), else the exception's.</summary>
    private static string Describe(Exception ex)
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
        stop.Cancel();
        stop.Dispose();
    }
}
