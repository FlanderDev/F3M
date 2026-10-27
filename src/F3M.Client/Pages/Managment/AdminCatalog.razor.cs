using F3M.Client.Shared;
using F3M.Shared.Models;

namespace F3M.Client.Pages.Managment;

public partial class AdminCatalog
{
    private bool rebuilding;
    private CatalogRebuildResult? rebuildResult;
    private string? rebuildError;

    private async Task RebuildCatalog()
    {
        rebuilding = true; rebuildError = null; rebuildResult = null;
        try
        {
            rebuildResult = await Api.RebuildCatalogAsync();
        }
        catch (Exception ex) { rebuildError = ApiErrors.Describe(ex); }
        finally { rebuilding = false; }
    }
}
