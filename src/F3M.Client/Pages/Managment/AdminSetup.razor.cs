using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

public partial class AdminSetup : IDisposable
{
    /// <summary>How often the page asks for progress while the framework import runs.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private SetupStatusDto? status;
    private ImportJobDto? job;
    private readonly BotSettingsUpdateDto form = new();
    private string? loadError;
    private string? actionError;
    private string? botMessage;
    private bool busy;
    private bool testing;

    private readonly CancellationTokenSource disposed = new();
    private bool polling;

    private bool JobRunning => job?.Running == true;

    /// <summary>Frameworks the site lacks that the import plan can bring.</summary>
    private int MissingInPlan => status?.Frameworks.Count(f => f.ModId is null && f.InPlan) ?? 0;

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync(fillForm: true);
    }

    private async Task RefreshAsync(bool fillForm = false)
    {
        try
        {
            status = await Api.GetStatusAsync(disposed.Token);
            job = (await ImportApi.GetStatusAsync(disposed.Token)).Job;
            loadError = null;
            if (fillForm)
                FillForm(status.Bot);
        }
        catch (OperationCanceledException) when (disposed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            loadError = DescribeError(ex);
        }

        if (JobRunning)
            _ = PollAsync();
    }

    /// <summary>Refreshes every second while an import step runs, once more when it ends.</summary>
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
                job = (await ImportApi.GetStatusAsync(disposed.Token)).Job;
                if (!JobRunning)
                    status = await Api.GetStatusAsync(disposed.Token);
                StateHasChanged();
                if (!JobRunning)
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

    private void FillForm(BotSettingsDto bot)
    {
        form.BaseUrl = bot.BaseUrl;
        form.Username = bot.Username;
        form.UserId = bot.UserId;
        form.Password = null;
    }

    private Task SaveBot() => RunAsync(async () =>
    {
        var saved = await Api.SaveBotAsync(form);
        FillForm(saved);
        botMessage = "Saved. Use \"Test login\" to check it.";
    });

    private Task RemoveBot() => RunAsync(async () =>
    {
        var bot = await Api.RemoveBotAsync();
        FillForm(bot);
        botMessage = bot.Source == BotSettingsSource.None ? "Removed." : "Removed; the F95Bot settings apply again.";
    });

    private async Task TestBot()
    {
        testing = true;
        await RunAsync(() => Api.TestBotAsync());
        testing = false;
    }

    private Task ImportFrameworks() => RunAsync(() => ImportApi.StartFrameworksAsync());

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        actionError = null;
        botMessage = null;
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

    private static string Icon(SetupCheckState state) => state switch
    {
        SetupCheckState.Done => "bi-check-circle-fill text-success",
        SetupCheckState.Problem => "bi-x-circle-fill text-danger",
        _ => "bi-circle text-warning",
    };

    private static string SourceLabel(BotSettingsSource source) => source switch
    {
        BotSettingsSource.Site => "Saved on this page",
        BotSettingsSource.Environment => "From the F95Bot settings",
        _ => "Not set up",
    };

    private static string SourceBadge(BotSettingsSource source) => source switch
    {
        BotSettingsSource.None => "bg-secondary",
        _ => "bg-info text-dark",
    };

    private static int Percent(ImportJobDto job) => job.Total == 0 ? 0 : (int)Math.Round(job.Done * 100.0 / job.Total);

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
