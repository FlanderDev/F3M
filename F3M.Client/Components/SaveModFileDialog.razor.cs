using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace F3M.Client.Components;

public partial class SaveModFileDialog
{
    // ── Parameters ────────────────────────────────────────────────────────────

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public string Title { get; set; } = "Save File";
    [Parameter] public string DefaultName { get; set; } = string.Empty;
    [Parameter] public string InitialPath { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    // ── Virtual filesystem ────────────────────────────────────────────────────
    private record FsNode(string Name, bool IsDir, List<FsNode> Children);

    private static FsNode GetTree() =>
        new(".", true,
            [
            new("SecretFlasherManaka.exe", false, []),
            new("BepInEx", true,
                [
                new("cache", true, []),
                new("config", true, []),
                new("core", true, []),
                new("interop", true, []),
                new("MapDump", true, []),
                new("patchers", true, []),
                new("SceneDumps", true, []),
                new("unity-libs", true, []),
                new("plugins", true,
                    [
                    new("FlanDev.SFM.IrlVibes", true, []),
                    new("sinai-dev-UnityExplorer", true,
                        [
                        new("Logs", true, []),
                        new("Scripts", true, [])
                        ])
                    ]),
                ]),
            new("dotnet", true, []),
            new("SecretFlasherManaka_Data", true,
                [
                new("il2cpp_data", true,
                    [
                    new("Metadata", true, []),
                    new("Resources", true, [])
                    ]),
                new("Plugins", true,
                    [
                    new("x86_64", true, [])
                    ]),
                new("Resources", true, []),
                new("StreamingAssets", true,
                    [
                    new("StandaloneWindows64", true, [])
                    ])
            ])
        ]);

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    private record Crumb(string Label, FsNode Node);

    private List<Crumb> _crumbs = [];
    private FsNode Current => _crumbs[^1].Node;

    // ── Component state ───────────────────────────────────────────────────────

    private FsNode? _selected;
    private string _fileName = string.Empty;
    private bool _newFolderMode;
    private string _newFolderName = string.Empty;
    private ElementReference _newFolderRef;
    private ElementReference _fileNameRef;
    private bool _initialised;

    // ── Computed path ─────────────────────────────────────────────────────────

    private string CurrentDir =>
        // Skip the root "/" label so paths look like "Manaka/Mods/Textures"
        string.Join("/", _crumbs.Skip(1).Select(c => c.Label));

    private string FullPath
    {
        get
        {
            var dir = CurrentDir;
            var name = _fileName.Trim();
            return string.IsNullOrEmpty(dir)
                ? name
                : string.IsNullOrEmpty(name) ? dir + "/" : $"{dir}/{name}";
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnParametersSet()
    {
        if (!Visible)
        {
            _initialised = false;   // reset so re-opening re-initialises
            return;
        }
        if (_initialised) return;
        _initialised = true;

        var root = GetTree();
        _crumbs = [new("/", root)];
        _fileName = DefaultName;
        _selected = null;
        _newFolderMode = false;
        _newFolderName = string.Empty;

        if (!string.IsNullOrWhiteSpace(InitialPath))
            NavigateToPath(InitialPath);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Focus the filename input when the dialog first appears
        if (Visible && _initialised)
        {
            try { await _fileNameRef.FocusAsync(); } catch { }
        }
    }

    // ── Path navigation ───────────────────────────────────────────────────────

    private void NavigateToPath(string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = Current.Children.FirstOrDefault(n =>
                n.IsDir && string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null) break;
            _crumbs.Add(new(child.Name, child));
        }
    }

    private void GoUp()
    {
        if (_crumbs.Count <= 1) return;
        _crumbs.RemoveAt(_crumbs.Count - 1);
        _selected = null;
    }

    private void NavigateToCrumb(int depth)
    {
        _crumbs = _crumbs[..(depth + 1)];
        _selected = null;
    }

    // ── Entry interaction ─────────────────────────────────────────────────────

    private void SelectNode(FsNode node)
    {
        _selected = node;
        _fileName = DefaultName;
        if (!node.IsDir)
            _fileName = node.Name;
    }

    private void ActivateNode(FsNode node)
    {
        if (node.IsDir)
        {
            _crumbs.Add(new(node.Name, node));
            _selected = null;
        }
        else
        {
            _fileName = node.Name;
        }
    }

    // ── New folder ────────────────────────────────────────────────────────────

    private async Task BeginNewFolder()
    {
        _newFolderMode = true;
        _newFolderName = string.Empty;
        await Task.Yield();
        try { await _newFolderRef.FocusAsync(); } catch { }
    }

    private void CommitNewFolder()
    {
        var name = _newFolderName.Trim();
        if (!string.IsNullOrEmpty(name) &&
            !Current.Children.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Current.Children.Add(new FsNode(name, true, []));
        }
        _newFolderMode = false;
        _newFolderName = string.Empty;
    }

    private void CancelNewFolder()
    {
        _newFolderMode = false;
        _newFolderName = string.Empty;
    }

    // ── Confirm / cancel ──────────────────────────────────────────────────────

    private async Task Confirm()
    {
        var path = FullPath.Trim('/');
        if (string.IsNullOrWhiteSpace(path)) return;
        await OnSave.InvokeAsync(path);
        await VisibleChanged.InvokeAsync(false);
    }

    private async Task Cancel()
    {
        await OnCancel.InvokeAsync();
        await VisibleChanged.InvokeAsync(false);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private async Task OnDialogKey(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape": await Cancel(); break;
            case "Enter" when !string.IsNullOrWhiteSpace(_fileName): await Confirm(); break;
            case "Backspace" when e.AltKey: GoUp(); break;
        }
    }

    private async Task OnFileNameKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_fileName))
            await Confirm();
    }

    private void OnNewFolderKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") CommitNewFolder();
        if (e.Key == "Escape") CancelNewFolder();
    }
}