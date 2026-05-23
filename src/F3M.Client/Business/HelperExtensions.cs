using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

namespace F3M.Client.Business;

public static class HelperExtensions
{
    internal static async Task<bool> IsAuthenticatedAsync(this Task<AuthenticationState>? authenticationState)
    {
        if (authenticationState is null)
            return false;

        return (await authenticationState)?.User.Identity?.IsAuthenticated ?? false;
    }

    public static string FileSize(this long bytes) =>
        bytes >= 1_048_576
            ? $"{bytes / 1_048_576.0:F1} MB"
            : $"{bytes / 1024.0:F1} KB";

    #region ServerAPI
    public static async Task<string[]> LoadCategoriesAsync(this HttpClient httpClient) => await httpClient.GetFromJsonAsync<string[]>(R.Mods.Categories) ?? [];
    public static async Task<ModVersionsResult?> LoadModVersionsAsync(this HttpClient httpClient, int groupId) => await httpClient.GetFromJsonAsync<ModVersionsResult>(R.Mods.GetVersions(groupId));
    #endregion
}