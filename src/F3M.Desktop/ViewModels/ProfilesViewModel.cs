using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop.ViewModels;

/// <summary>Create, duplicate, capture, rename, delete and share local profiles (plan 6.9, 10.2).</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly MainViewModel _shell;

    public ProfilesViewModel(AppServices app, MainViewModel shell)
    {
        _app = app;
        _shell = shell;
        _shell.StateChanged += (_, _) => Reload();
    }

    public ObservableCollection<ProfileDef> Items { get; } = [];
    public ObservableCollection<string> Members { get; } = [];

    [ObservableProperty]
    private ProfileDef? _selected;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _shareLink = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    partial void OnSelectedChanged(ProfileDef? value)
    {
        EditName = value?.Name ?? string.Empty;
        ShareLink = string.Empty;
        Members.Clear();
        if (value is null) return;

        foreach (var groupId in value.GroupIds)
            Members.Add(_app.Catalog.Group(groupId)?.Name ?? $"Mod {groupId} (not in the catalog)");
    }

    public void Reload()
    {
        var keep = Selected?.Id;
        Items.Clear();
        foreach (var profile in _app.Profiles.List()) Items.Add(profile);
        Selected = Items.FirstOrDefault(p => p.Id == keep) ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    private void NewProfile()
    {
        var profile = _app.Profiles.Create("New profile");
        Status = $"Created {profile.Name}. Add mods from Browse.";
        _ = _shell.RefreshStateAsync();
        Reload();
        Selected = Items.FirstOrDefault(p => p.Id == profile.Id);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var copy = _app.Profiles.Duplicate(Selected);
        Status = $"Duplicated as {copy.Name}.";
        _ = _shell.RefreshStateAsync();
        Reload();
        Selected = Items.FirstOrDefault(p => p.Id == copy.Id);
    }

    [RelayCommand]
    private void CaptureCurrent()
    {
        var captured = _app.Profiles.CaptureDeployed();
        if (captured.GroupIds.Count == 0)
        {
            Status = "Nothing is deployed yet, so there is nothing to capture.";
            return;
        }

        Status = $"Captured {captured.GroupIds.Count} mod(s) as {captured.Name}.";
        _ = _shell.RefreshStateAsync();
        Reload();
        Selected = Items.FirstOrDefault(p => p.Id == captured.Id);
    }

    [RelayCommand]
    private void Rename()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(EditName)) return;
        Selected.Name = EditName.Trim();
        _app.Profiles.Save(Selected);
        Status = "Renamed.";
        _ = _shell.RefreshStateAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        if (Selected.Id == _shell.ActiveProfile?.Id && _app.Deploy.LoadState().ProfileId == Selected.Id)
        {
            Status = "This profile is deployed. Switch to another profile before deleting it.";
            return;
        }

        if (!await _shell.ConfirmAsync("Delete profile", $"Delete {Selected.Name}? Cached mods are kept.", "Delete")) return;
        _app.Profiles.Delete(Selected);
        Status = "Deleted.";
        Selected = null;
        Reload();
        await _shell.RefreshStateAsync();
    }

    /// <summary>Switches the game folder to this profile through the shell's switch flow, with its summary dialog.</summary>
    [RelayCommand]
    private void Switch()
    {
        if (Selected is not null) _shell.ProfileSelection = Selected;
    }

    [RelayCommand]
    private void Share()
    {
        if (Selected is null) return;
        try
        {
            ShareLink = _app.Profiles.WebLink(Selected);
            Status = "Share link ready. Anyone with it can read the mod list.";
        }
        catch (UserException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (string.IsNullOrEmpty(ShareLink)) return;
        _app.CopyText(ShareLink);
        Status = "Link copied.";
    }
}
