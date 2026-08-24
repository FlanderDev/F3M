using F3M.Client.Components;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using FlanderDev.RouteGen;
using System.Net;
using System.Security.Claims;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Client.Pages.Modifications;

public partial class EditMod
{
    [Parameter] public int Id { get; set; }
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private Mod? mod;
    private ModEditDto dto = new();
    private string[] Categories = [];
    private bool loading = true;
    private bool forbidden = false;
    private bool saving = false;
    private bool saved = false;
    private string? saveError;

    // Pre-populated from mod.Dependencies (the resolved view GetMod already returns) so the
    // picker shows what's currently set, not an empty selection — mirrors Upload.razor's
    // tracking, just seeded instead of starting empty.
    private List<MultiSelectDropdown<Mod>.InternalItem> selectedDependencyItems = [];

    private void SetDependencies(List<MultiSelectDropdown<Mod>.InternalItem> selected)
    {
        selectedDependencyItems = selected;
        dto.DependencyGroupIds = [.. selected.Select(s => s.Model.ModGroupId).Distinct()];
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            Categories = [.. await ModsApi.GetCategories()];
            mod = await ModsApi.GetMod(Id);

            // Check ownership or admin
            if (AuthState is not null)
            {
                var state = await AuthState;
                var userIdStr = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var isAdmin = state.User.IsInRole(AppRoles.Admin);

                if (!isAdmin)
                {
                    // Need to check group ownership
                    var group = await ModsApi.GetVersions(mod.ModGroupId);
                    var ownerId = group.Group.OwnerId;
                    if (!int.TryParse(userIdStr, out var userId) || ownerId != userId)
                    {
                        forbidden = true;
                        return;
                    }
                }
            }

            dto = new ModEditDto
            {
                Name = mod.Name,
                Description = mod.Description,
                Version = mod.Version,
                Category = mod.Category,
                DependencyGroupIds = [.. mod.Dependencies.Select(d => d.ModGroupId)]
            };
            selectedDependencyItems = [.. mod.Dependencies.Select(d => new MultiSelectDropdown<Mod>.InternalItem(d, false))];
        }
        catch { mod = null; }
        finally { loading = false; }
    }

    private async Task<List<Mod>?> LoadMultiSelectDropdownValues(string searchText)
        => await ModsApi.SearchMods(searchText);

    private async Task HandleSave()
    {
        saving = true; saveError = null; saved = false;
        try
        {
            mod = await ModsApi.Edit(Id, dto);
            saved = true;
        }
        catch (ApiException ex)
        {
            saveError = ex.StatusCode == HttpStatusCode.Forbidden
                ? "You don't have permission to edit this mod."
                : $"Save failed: {ex.ResponseBody}";
        }
        catch (Exception ex) { saveError = ex.Message; }
        finally { saving = false; }
    }
}