using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using F3M.Desktop.Services;
using F3M.Desktop.ViewModels;

namespace F3M.Desktop.Views;

/// <summary>
/// The Library's drop zone for archives. While a file is dragged over it, it shows whether the file can be added; a
/// drop or a click hands the file to <see cref="LibraryViewModel.AddArchiveAsync"/>.
/// </summary>
public partial class ArchiveDropZone : UserControl
{
    private const string DefaultTitle = "Drop a .zip, .7z, .rar or .dll here, or click to choose a file";

    public ArchiveDropZone()
    {
        InitializeComponent();
        Zone.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        Zone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Zone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Reset());
        Zone.AddHandler(DragDrop.DropEvent, OnDrop);
        Zone.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) _ = Library?.AddArchiveAsync(null);
        };
    }

    private LibraryViewModel? Library => DataContext as LibraryViewModel;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var path = DraggedPath(e);
        var accepted = path is not null && LocalMods.CanAdd(path);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        Zone.Classes.Set("over", accepted);
        Zone.Classes.Set("refused", !accepted);
        Title.Text = accepted
            ? $"Drop to add {Path.GetFileName(path)}"
            : "Only a .zip, .7z, .rar or .dll file can be added";
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var path = DraggedPath(e);
        Reset();
        e.Handled = true;
        if (path is not null) _ = Library?.AddArchiveAsync(path);
    }

    private void Reset()
    {
        Zone.Classes.Set("over", false);
        Zone.Classes.Set("refused", false);
        Title.Text = DefaultTitle;
    }

    /// <summary>The first dragged file on this computer. Folders and items without a local path give null.</summary>
    private static string? DraggedPath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
}
