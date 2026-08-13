using F3M.Client.Business;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

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

    protected override async Task OnInitializedAsync()
    {
        try
        {
            Categories = await Http.LoadCategoriesAsync();
            mod = await Http.GetFromJsonAsync<Mod>(Endpoints.Mods.GetMod(Id));
            if (mod is null)
                return;

            // Check ownership or admin
            if (AuthState is not null)
            {
                var state = await AuthState;
                var userIdStr = state.User.FindFirst("sub")?.Value;
                var isAdmin = state.User.IsInRole(AppRoles.Admin);

                if (!isAdmin)
                {
                    // Need to check group ownership
                    var group = await Http.GetFromJsonAsync<ModVersionsResult>(Endpoints.Mods.GetVersions(mod.ModGroupId));
                    var ownerId = group?.Group.OwnerId;
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
                Category = mod.Category
            };
        }
        catch { mod = null; }
        finally { loading = false; }
    }

    private async Task HandleSave()
    {
        saving = true; saveError = null; saved = false;
        try
        {
            var resp = await Http.PutAsJsonAsync(Endpoints.Mods.GetMod(Id), dto);
            if (resp.IsSuccessStatusCode)
            {
                saved = true;
                mod = await resp.Content.ReadFromJsonAsync<Mod>();
            }
            else
            {
                saveError = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "You don't have permission to edit this mod."
                    : $"Save failed: {await resp.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex) { saveError = ex.Message; }
        finally { saving = false; }
    }
}