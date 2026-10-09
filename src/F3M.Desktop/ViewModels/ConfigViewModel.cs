using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Shared.Models;

namespace F3M.Desktop.ViewModels;

/// <summary>One setting in the editor: a control for its type, validation, and the reset action.</summary>
public sealed partial class CfgSettingVm : ObservableObject
{
    public CfgSettingVm(CfgSetting setting)
    {
        Setting = setting;
        _text = setting.Value;
        _flag = setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        _choice = setting.Choices.FirstOrDefault(c => c.Equals(setting.Value, StringComparison.OrdinalIgnoreCase))
                  ?? setting.Value;
        ResetCommand = new RelayCommand(ResetToDefault);
        Revalidate();
    }

    public CfgSetting Setting { get; }
    public string Key => Setting.Key;
    public string Description => string.IsNullOrWhiteSpace(Setting.Description)
        ? "No description in the file. Showing the raw key."
        : Setting.Description;
    public string Hint
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Setting.DefaultValue)) parts.Add($"Default: {Setting.DefaultValue}");
            if (Setting.Min is not null || Setting.Max is not null) parts.Add($"Range: {Setting.Min} to {Setting.Max}");
            return string.Join("   ", parts);
        }
    }

    public IReadOnlyList<string> Choices => Setting.Choices;
    public bool IsBoolean => Setting.Kind == CfgKind.Boolean;
    public bool IsChoice => Setting.Kind == CfgKind.Choice;
    public bool IsText => Setting.Kind is CfgKind.Text or CfgKind.Number;
    public bool ChangedFromDefault => Setting.ChangedFromDefault;
    public IRelayCommand ResetCommand { get; }

    /// <summary>The value as it will be written.</summary>
    public string ValueText => IsBoolean ? (Flag ? "true" : "false") : IsChoice ? (Choice ?? string.Empty) : Text;

    public bool Dirty => !string.Equals(ValueText, Setting.OriginalValue, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(Dirty))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(Dirty))]
    private bool _flag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(Dirty))]
    private string? _choice;

    [ObservableProperty]
    private string? _error;

    partial void OnTextChanged(string value) => Revalidate();

    partial void OnFlagChanged(bool value) => Revalidate();

    partial void OnChoiceChanged(string? value) => Revalidate();

    public void Revalidate() => Error = Setting.Validate(ValueText);

    public void ResetToDefault()
    {
        var value = Setting.DefaultValue;
        if (value is null) return;

        if (IsBoolean) Flag = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        else if (IsChoice) Choice = Setting.Choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;
        else Text = value;
    }
}

/// <summary>The config editor for one mod's deployed .cfg files (plan 8).</summary>
public sealed partial class ConfigViewModel(AppServices app) : ObservableObject
{
    private CfgDocument? _document;
    private string _path = string.Empty;

    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<CfgSettingVm> Settings { get; } = [];

    [ObservableProperty]
    private string? _selectedFile;

    [ObservableProperty]
    private string _title = "Config";

    [ObservableProperty]
    private string _status = string.Empty;

    partial void OnSelectedFileChanged(string? value) => Load(value);

    /// <summary>Lists the config files a deployed mod placed and opens the first one.</summary>
    public void Open(DeployedMod mod)
    {
        Title = $"Config: {mod.Name}";
        Files.Clear();
        foreach (var file in mod.Files.Where(f => f.Kind == "config")) Files.Add(file.To);

        if (Files.Count == 0)
        {
            SelectedFile = null;
            Status = "This mod has no config files in the game folder.";
            return;
        }

        SelectedFile = Files[0];
    }

    private void Load(string? relative)
    {
        Settings.Clear();
        _document = null;
        Status = string.Empty;
        if (relative is null) return;

        try
        {
            _path = FileOps.ResolveUnder(app.Game.RootOrThrow(), relative);
            if (!File.Exists(_path))
            {
                Status = "The file is missing from the game folder. Deploy the profile again.";
                return;
            }

            _document = CfgDocument.Parse(File.ReadAllText(_path));
            foreach (var setting in _document.Settings) Settings.Add(new CfgSettingVm(setting));
            Status = Settings.Count == 0
                ? "No settings were found in this file. Edit it with a text editor instead."
                : $"{Settings.Count} setting(s).";
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var document = _document;
        if (document is null) return;

        if (app.Game.IsRunning())
        {
            Status = "Close the game before saving. BepInEx reads config files at startup.";
            return;
        }

        if (Settings.Any(s => s.Error is not null))
        {
            Status = "Fix the highlighted values first.";
            return;
        }

        var changed = Settings.Where(s => s.Dirty).ToList();
        if (changed.Count == 0)
        {
            Status = "No changes to save.";
            return;
        }

        try
        {
            Directory.CreateDirectory(app.Paths.ConfigBackups);
            var backup = Path.Combine(app.Paths.ConfigBackups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(_path)}");
            File.Copy(_path, backup, overwrite: true);

            foreach (var setting in changed) document.SetValue(setting.Setting, setting.ValueText);
            FileOps.WriteTextAtomic(_path, document.ToText());

            var file = SelectedFile;
            Load(file);
            Status = $"Saved {changed.Count} change(s). The previous file is in the config backups.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not save {_path}", ex);
            Status = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetFile()
    {
        foreach (var setting in Settings) setting.ResetToDefault();
    }
}
