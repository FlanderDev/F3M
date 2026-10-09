using F3M.Client.Components;
using F3M.Client.Services;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace F3M.Client.Pages.Collections;

/// <summary>
/// Read-only view of a shared collection (plan 10.3). Reads <c>mods</c> and <c>name</c> from the query string.
/// It writes nothing and needs no sign-in.
/// </summary>
public partial class Collection : ComponentBase, IDisposable
{
    [Inject] private CatalogClient Catalog { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    [SupplyParameterFromQuery] public string? Mods { get; set; }
    [SupplyParameterFromQuery] public string? Name { get; set; }

    private CollectionLink? _link;
    private CollectionView? _view;
    private string? _error;
    private bool _desktopMissing;
    private bool _copied;
    private CancellationTokenSource? _loadCts;

    protected override async Task OnParametersSetAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _view = null;
        _desktopMissing = false;
        _copied = false;

        if (!CollectionLink.TryParse(Mods, Name, out var link, out var error))
        {
            _link = null;
            _error = error;
            return;
        }

        _link = link;
        _error = null;
        try
        {
            _view = await Catalog.LoadCollectionAsync(link!.GroupIds, ct);
        }
        catch (OperationCanceledException)
        {
            // A newer query arrived; its load will fill the page.
        }
        catch (HttpRequestException)
        {
            _error = "The catalog could not be loaded. Try again in a moment.";
        }
    }

    /// <summary>Opens the protocol link. The JS side reports whether the desktop app took it.</summary>
    private async Task UseSetupAsync()
    {
        if (_link is null) return;
        _desktopMissing = !await JS.InvokeAsync<bool>("f3m.openInDesktop", _link.ToProtocolUri());
    }

    private async Task CopyLinkAsync()
    {
        if (_link is null) return;
        var url = Nav.ToAbsoluteUri(_link.ToWebPath()).ToString();
        _copied = await JS.InvokeAsync<bool>("f3m.copyText", url);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
