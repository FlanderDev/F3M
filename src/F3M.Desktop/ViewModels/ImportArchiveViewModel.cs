using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.ViewModels;

/// <summary>
/// The preview before an archive is added as a local mod: what F3M detected, where every file would go, and the
/// upload form's warnings. Every change recomputes the list, nothing is written before Add.
/// </summary>
public sealed partial class ImportArchiveViewModel : ObservableObject
{
    /// <summary>The list shows this many paths at most; the count above it is always the full one.</summary>
    private const int MaxListed = 500;

    private readonly AppServices _app;
    private readonly LocalModDraft _analyzed;

    public ImportArchiveViewModel(AppServices app, LocalModDraft draft)
    {
        _app = app;
        _analyzed = draft;
        _name = draft.Name;
        _template = ModTemplates.Find(draft.TemplateId) ?? ModTemplates.Find(ModTemplates.Custom)!;
        _installPath = draft.InstallPath;
        _archiveRoot = draft.ArchiveRoot;
        Refresh();
    }

    /// <summary>Raised with the added mod, or null when the window should close without adding anything.</summary>
    public event Action<VersionDocument?>? CloseRequested;

    public string Title => $"Add {_analyzed.FileName}";
    public string SourcePath => _analyzed.SourcePath;
    public bool IsArchive => _analyzed.Entries is not null;
    public IReadOnlyList<ModTemplate> Templates => ModTemplates.All;
    public ObservableCollection<string> Targets { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private ModTemplate _template;

    [ObservableProperty]
    private string _installPath;

    [ObservableProperty]
    private string _archiveRoot;

    [ObservableProperty]
    private string _targetsSummary = string.Empty;

    /// <summary>The upload form's hints for this type, one per line. Empty when everything looks right.</summary>
    [ObservableProperty]
    private string _warnings = string.Empty;

    /// <summary>The mod this type needs and whether the catalog has it. Empty when the type needs nothing.</summary>
    [ObservableProperty]
    private string _requirement = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _canAdd;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isBusy;

    public LocalModDraft Draft => _analyzed with
    {
        Name = Name,
        TemplateId = Template.Id,
        InstallPath = InstallPath.Trim(),
        ArchiveRoot = ArchiveRoot.Trim(),
    };

    partial void OnNameChanged(string value) => Refresh();
    partial void OnTemplateChanged(ModTemplate value) => Refresh();
    partial void OnInstallPathChanged(string value) => Refresh();
    partial void OnArchiveRootChanged(string value) => Refresh();

    [RelayCommand(CanExecute = nameof(CanRunAdd))]
    private async Task AddAsync()
    {
        IsBusy = true;
        Status = "Adding…";
        var draft = Draft;
        VersionDocument? added = null;
        OperationItem op;
        try
        {
            op = await _app.Ops.RunAsync("Add", $"Add {draft.Name.Trim()}", _app.Ops.DownloadGate,
                async (item, ct) => added = await _app.LocalMods.AddAsync(draft, item, ct));
        }
        finally
        {
            // Before closing: the window refuses to close while busy.
            IsBusy = false;
        }

        if (op.Status == "Done" && added is not null)
        {
            CloseRequested?.Invoke(added);
            return;
        }

        Status = op.Detail;
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private bool CanRunAdd() => CanAdd && !IsBusy;

    private void Refresh()
    {
        var draft = Draft;
        var targets = draft.Targets();
        Targets.Clear();
        if (targets is null)
        {
            TargetsSummary = IsArchive ? "The install path or the archive folder is not valid." : "The install path is not valid.";
            Warnings = string.Empty;
            CanAdd = false;
        }
        else
        {
            foreach (var target in targets.Take(MaxListed)) Targets.Add(target);
            TargetsSummary = targets.Count switch
            {
                0 when IsArchive && ArchiveRoot.Trim().Length > 0 => $"Nothing in the archive is inside '{ArchiveRoot.Trim()}'.",
                0 => "Nothing would be installed.",
                1 => "1 file goes into the game folder:",
                _ when targets.Count > MaxListed => $"{targets.Count} files go into the game folder (the first {MaxListed} are listed):",
                _ => $"{targets.Count} files go into the game folder:",
            };
            Warnings = string.Join("\n", ModTemplates.Check(Template.Id, targets));
            CanAdd = targets.Count > 0 && Name.Trim().Length > 0;
        }

        Requirement = _app.LocalMods.RequiredMod(Template.Id) switch
        {
            null => string.Empty,
            { Group: not null } required => $"Needs {required.Name}. It is added from the catalog with this mod.",
            var required => $"Needs {required.Value.Name}, which is not in the catalog. Install it yourself, or this mod won't load.",
        };
        if (!IsBusy) Status = Name.Trim().Length == 0 ? "Give the mod a name." : string.Empty;
    }
}
