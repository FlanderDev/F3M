using Avalonia;
using Avalonia.Controls;

namespace F3M.Desktop.Tray;

/// <summary>
/// The tray icon and its menu (plan 3.5): Open, Play, the Profiles submenu with the active one ticked, and Exit.
/// Built on Avalonia's TrayIcon and NativeMenu, no third-party tray library.
/// </summary>
public sealed class TrayMenu
{
    // 32x32 amber "F" mark, generated for this app (see the plan's visual identity).
    private const string IconPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAZ0lEQVR42mNgwAP+H2T/Tw3MQAqglqVkOYbWluN1BL0sx+oIeluO4YiR7YCBshzuCGIU8fIJkIVHHTB8HTDgiXDUAZTG+6gDRhPhqANGHUCyA4Ztq2i0UTp4OiaDoms2KDqnA9E9BwA2cHyUKa28BwAAAABJRU5ErkJggg==";

    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _profiles;
    private readonly Action<string> _switchTo;

    public TrayMenu(Application application, Action open, Action play, Action<string> switchTo, Action exit)
    {
        _switchTo = switchTo;

        var openItem = new NativeMenuItem("Open");
        openItem.Click += (_, _) => open();

        var playItem = new NativeMenuItem("Play");
        playItem.Click += (_, _) => play();

        _profiles = new NativeMenuItem("Profiles") { Menu = new NativeMenu() };

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(playItem);
        menu.Items.Add(_profiles);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _icon = new TrayIcon
        {
            Icon = CreateIcon(),
            ToolTipText = "F3M Desktop",
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => open();

        var icons = new TrayIcons();
        icons.Add(_icon);
        TrayIcon.SetIcons(application, icons);
    }

    public static WindowIcon CreateIcon()
    {
        var bytes = Convert.FromBase64String(IconPngBase64);
        return new WindowIcon(new MemoryStream(bytes));
    }

    /// <summary>Rebuilds the Profiles submenu and the tooltip. Called whenever the profile list or the deployed state changes.</summary>
    public void Refresh(IReadOnlyList<(string Id, string Name)> profiles, string? activeId, string tooltip)
    {
        _icon.ToolTipText = tooltip;

        var items = _profiles.Menu!.Items;
        items.Clear();
        if (profiles.Count == 0)
        {
            items.Add(new NativeMenuItem("No profiles yet") { IsEnabled = false });
            return;
        }

        foreach (var (id, name) in profiles)
        {
            var target = id;
            var item = new NativeMenuItem((id == activeId ? "✓ " : "   ") + name);
            item.Click += (_, _) => _switchTo(target);
            items.Add(item);
        }
    }

    public void Hide() => _icon.IsVisible = false;
}
