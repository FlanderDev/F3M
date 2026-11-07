using Avalonia.Controls;
using F3M.Desktop.ViewModels;
using F3M.Shared.Models;

namespace F3M.Desktop.Views;

/// <summary>The preview before an archive is added as a local mod. Closes with the added mod, or null.</summary>
public partial class ImportArchiveWindow : Window
{
    public ImportArchiveWindow()
    {
        InitializeComponent();
    }

    public static Task<VersionDocument?> ShowAsync(Window owner, ImportArchiveViewModel model)
    {
        var window = new ImportArchiveWindow { DataContext = model };
        model.CloseRequested += added => window.Close(added);

        // While the files are copied, closing would leave a mod that is in no profile.
        window.Closing += (_, e) => e.Cancel |= model.IsBusy;
        return window.ShowDialog<VersionDocument?>(owner);
    }
}
