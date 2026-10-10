using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop.ViewModels;

/// <summary>The operation list (plan 4.2) and the cache summary (plan 6.11).</summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly AppServices _app;

    public DownloadsViewModel(AppServices app)
    {
        _app = app;
        RefreshCacheInfo();
    }

    public ObservableCollection<OperationItem> Items => _app.Ops.Items;

    [ObservableProperty]
    private string _cacheInfo = string.Empty;

    [RelayCommand]
    private void ClearFinished() => _app.Ops.ClearFinished();

    [RelayCommand]
    private void RefreshCacheInfo()
    {
        try
        {
            var (count, bytes) = _app.Downloads.UnusedCache();
            CacheInfo = $"Cache: {FileOps.FormatBytes(FileOps.DirectorySize(_app.Paths.Cache))}. " +
                        $"Unused: {count} version(s), {FileOps.FormatBytes(bytes)}.";
        }
        catch (UserException)
        {
            CacheInfo = "Cache size is not available until the deployed-state record is repaired.";
        }
    }
}
