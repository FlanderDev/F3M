using F3M.Client.Business;
using F3M.Client.Components;
using F3M.Client.Models;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
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

    // ── Preview image ─────────────────────────────────────────────────────────

    // The picker shows the current image until the author picks another or clears it.
    private byte[]? imageBytes;
    private string imageFileName = string.Empty;
    private string? previewDataUrl;

    // ── Optional files ────────────────────────────────────────────────────────

    private readonly List<OptionalGroupDraft> optionalGroups = [];

    // Edits never suggest groups; the set is only what the editor needs.
    private readonly HashSet<string> dismissedSuggestions = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> ShippedPaths => mod?.ShippedPaths ?? [];

    private OptionalGroupsCheck OptionalCheck => OptionalGroupChecks.Evaluate(optionalGroups, ShippedPaths);

    // ── Required mods (frameworks) ────────────────────────────────────────────

    // Required mods by name (ModTemplate.RequiredModName), looked up once; null when the site doesn't have it.
    private readonly Dictionary<string, Mod?> requiredMods = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<ModTemplate> UsedTemplates =>
        (mod?.Files ?? []).Select(f => ModTemplates.Find(f.Template)).OfType<ModTemplate>().DistinctBy(t => t.Id);

    /// <summary>The frameworks this version's file types need that are on the site but not listed, like on upload.</summary>
    private List<string> RequirementWarnings
    {
        get
        {
            var warnings = new List<string>();
            foreach (var template in UsedTemplates.Where(t => t.RequiredModName is not null))
            {
                if (requiredMods.GetValueOrDefault(template.RequiredModName!) is { } required
                    && required.ModGroupId != mod?.ModGroupId
                    && !selectedDependencyItems.Any(i => i.Model.ModGroupId == required.ModGroupId))
                {
                    warnings.Add($"{template.Name} files need {template.RequiredModName}. Add it as a requirement so that players get it too.");
                }
            }

            return warnings;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            mod = await ModsApi.GetMod(Id);

            // Like the upload page: the default categories, then any other the site uses, and the mod's own; each once.
            Categories = [.. F3M.Shared.Helpers.Categories.OthersLast(
                Configuration.DefaultCategories.Concat(await ModsApi.GetCategories()).Append(mod.Category))];

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
                DependencyGroupIds = [.. mod.Dependencies.Select(d => d.ModGroupId)],
                TagIds = [.. mod.TagIds]
            };
            selectedDependencyItems = [.. mod.Dependencies.Select(d => new MultiSelectDropdown<Mod>.InternalItem(d, false))];
            ShowSaved(mod);

            foreach (var name in UsedTemplates.Select(t => t.RequiredModName).OfType<string>().Distinct())
            {
                var found = await ModsApi.SearchMods(name);
                requiredMods[name] = found?.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { mod = null; }
        finally { loading = false; }
    }

    /// <summary>Shows the saved image and optional groups of <paramref name="saved"/> in the form.</summary>
    private void ShowSaved(Mod saved)
    {
        imageBytes = null;
        imageFileName = string.Empty;
        previewDataUrl = saved.PreviewImageName is { Length: > 0 } image ? image.ImageUrl() : null;

        optionalGroups.Clear();
        optionalGroups.AddRange(saved.OptionalGroups.Select(g => new OptionalGroupDraft { Name = g.Name, Patterns = [.. g.Patterns] }));
    }

    private async Task<List<Mod>?> LoadMultiSelectDropdownValues(string searchText)
        => await ModsApi.SearchMods(searchText);

    private async Task HandleSave()
    {
        if (mod is null || OptionalCheck.Error is not null)
            return;

        saving = true; saveError = null; saved = false;
        try
        {
            dto.OptionalGroups = [.. optionalGroups.Select(g => g.ToDto())];
            dto.PreviewImageBase64 = imageBytes is { Length: > 0 } ? Convert.ToBase64String(imageBytes) : null;
            dto.PreviewImageFileName = imageBytes is { Length: > 0 } ? imageFileName : null;
            dto.RemovePreviewImage = imageBytes is null && previewDataUrl is null && mod.PreviewImageName is not null;

            mod = await ModsApi.Edit(Id, dto);
            ShowSaved(mod);
            saved = true;
        }
        catch (ApiException ex)
        {
            saveError = ex.StatusCode == HttpStatusCode.Forbidden
                ? "You don't have permission to edit this mod."
                : $"Save failed: {Message(ex.ResponseBody)}";
        }
        catch (Exception ex) { saveError = ex.Message; }
        finally
        {
            saving = false;
            dto.PreviewImageBase64 = null; // a few MB; only needed for the request
        }
    }

    /// <summary>The server's message from a 400 (a JSON string), or the body as it is.</summary>
    private static string? Message(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return body;

        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
