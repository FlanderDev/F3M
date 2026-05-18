using System.Globalization;

namespace F3M.Shared;

/// <summary>
/// Contains all API routes, to avoid magic strings in controllers and client code.
/// </summary>
/// <remarks>
/// Really want to avoid magic values.
/// </remarks>
public struct R
{
    #region Parts of paths
    public const string Api = "api";
    public const string Group = "group";
    public const string Versions = "versions";
    public const string Categories = "categories";
    public const string Upload = "upload";
    public const string Download = "download";
    public const string Users = "users";
    #endregion

    public struct Admin
    {
        public const string Base = $"{Api}/admin";
        public const string GetUsers = $"{Base}/users";
        public static string ToggleAdmin(int id) => $"{Base}/{id}";
        public static string DeleteUser(int id) => $"{Base}/{id}";
    }

    public struct Auth
    {
        public const string Base = $"{Api}/auth";
        public const string Register = $"{Base}/register";
        public const string Login = $"{Base}/login";
    }

    public struct Mods
    {
        public const string Base = $"{Api}/mods";
        public const string Upload = $"{Base}/upload";
        public const string Categories = $"{Base}/categories";
        public static string GetMod(int id) => $"{Base}/{id}";
        public static string GetMods(int currentPage, int pageSize, string searchTerm, string selectedCategory, Configuration.SortBy sortBy) => string.Join(string.Empty,
            Base, "?",
            $"page={currentPage}&pageSize={pageSize}",
            $"&search={Uri.EscapeDataString(searchTerm)}",
            $"&sort={sortBy}",
            string.IsNullOrWhiteSpace(selectedCategory)
            ? string.Empty
            : $"&category={Uri.EscapeDataString(selectedCategory)}");
        public static string GetVersions(int groupId) => $"{Base}/{groupId}/versions";
        public static string Download(int versionId, int fileId) => $"{Base}/{versionId}/download/{fileId}";
    }
}
