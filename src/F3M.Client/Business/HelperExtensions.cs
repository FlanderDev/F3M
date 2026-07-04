using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Ganss.Xss;
using Markdig;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

namespace F3M.Client.Business;

public static class HelperExtensions
{
    #region ServerAPI
    public static async Task<string[]> LoadCategoriesAsync(this HttpClient httpClient) => await httpClient.GetFromJsonAsync<string[]>(Endpoints.Mods.Categories) ?? [];
    public static async Task<ModVersionsResult?> LoadModVersionsAsync(this HttpClient httpClient, int groupId) => await httpClient.GetFromJsonAsync<ModVersionsResult>(Endpoints.Mods.GetVersions(groupId));
    #endregion

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

    public static string SanatizeMarkdownToHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

        var html = Markdown.ToHtml(markdown, pipeline);

        var sanitizer = new HtmlSanitizer();
        string[] headings = ["h1", "h2", "h3", "h4", "h5"];
        string[] textFormatting = ["p", "br", "strong", "em", "b", "i"];
        string[] lists = ["ul", "ol", "li"];
        string[] code = ["code", "pre"];
        string[] media = ["img"];
        string[] structure = ["blockquote", "hr"];
        string[] table = ["table", "tbody", "thead", "tr", "th", "td"];

        ReplaceSet(sanitizer.AllowedTags, ["a", .. headings, .. textFormatting, .. lists, .. code, .. media, .. structure, .. table]);
        ReplaceSet(sanitizer.AllowedAttributes, ["src", "href", "title", "alt"]);
        ReplaceSet(sanitizer.AllowedSchemes, ["http", "https", "mailto"]);

        var safeHtml = sanitizer.Sanitize(html);
        return safeHtml;

        static void ReplaceSet(ISet<string> set, params IEnumerable<string> strings)
        {
            set.Clear();
            foreach (var item in strings)
                set.Add(item);
        }
    }
}