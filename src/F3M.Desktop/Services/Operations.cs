using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;

namespace F3M.Desktop.Services;

/// <summary>One visible operation (a download, a deploy). Progress is reported from worker threads and marshalled to the UI.</summary>
public sealed partial class OperationItem : ObservableObject
{
    public OperationItem(string kind, string title)
    {
        Kind = kind;
        Title = title;
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Kind { get; }
    public string Title { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public IRelayCommand CancelCommand { get; }

    /// <summary>Queued, Running, Done, Failed or Cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished), nameof(CanCancel))]
    private string _status = "Queued";

    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    private double _progress;

    public bool IsFinished => Status is "Done" or "Failed" or "Cancelled";
    public bool CanCancel => !IsFinished;

    public void SetDetail(string text) => Dispatcher.UIThread.Post(() => Detail = text);

    public void Report(long done, long total) => Dispatcher.UIThread.Post(() =>
    {
        Progress = total > 0 ? done * 100.0 / total : 0;
        Detail = $"{FileOps.FormatBytes(done)} of {FileOps.FormatBytes(total)}";
    });

    public void Cancel() => Cancellation.Cancel();

    public Task FinishAsync(string status, string detail) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        Status = status;
        Detail = detail;
        if (status == "Done") Progress = 100;
    });
}

/// <summary>
/// Runs operations with visible status. Each kind of work has a gate: two downloads at a time, one deploy at a time
/// (plan 6.12). Call from the UI thread; the work itself runs on the thread pool.
/// </summary>
public sealed class OperationQueue
{
    public ObservableCollection<OperationItem> Items { get; } = [];

    public SemaphoreSlim DownloadGate { get; } = new(2, 2);
    public SemaphoreSlim DeployGate { get; } = new(1, 1);

    public async Task<OperationItem> RunAsync(
        string kind, string title, SemaphoreSlim gate, Func<OperationItem, CancellationToken, Task> work)
    {
        var item = new OperationItem(kind, title);
        Items.Add(item);

        var acquired = false;
        try
        {
            await gate.WaitAsync(item.Cancellation.Token);
            acquired = true;
            item.Status = "Running";
            await Task.Run(() => work(item, item.Cancellation.Token), item.Cancellation.Token);
            await item.FinishAsync("Done", "Done");
        }
        catch (OperationCanceledException)
        {
            await item.FinishAsync("Cancelled", "Cancelled");
        }
        catch (UserException ex)
        {
            await item.FinishAsync("Failed", ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{kind} failed", ex);
            await item.FinishAsync("Failed", "Unexpected error. The details are in the diagnostics log (Settings).");
        }
        finally
        {
            if (acquired) gate.Release();
        }

        return item;
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList()) Items.Remove(item);
    }
}
