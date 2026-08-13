using System.Text;

namespace F3M.Shared.Helpers;

/// <summary>
/// Contains all API routes, to avoid magic strings in controllers and client code.
/// </summary>
public static class Endpoints
{
    #region Parts of paths
    public const string Api = "api";
    public const string Administrator = "admin";
    public const string Group = "group";
    public const string Versions = "versions";
    public const string Categories = "categories";
    public const string Upload = "upload";
    public const string Download = "download";
    public const string Modifications = "mods";
    public const string Users = "users";
    public const string Authentication = "auth";
    public const string F95 = "f95";
    public const string Start = "start";
    public const string Check = "check";
    public const string Telemetry = "telemetry";
    public const string Error = "error";
    #endregion

    public static class Admin
    {
        public const string Base = $"{Api}/{Administrator}";
        public const string GetUsers = $"{Base}/{Users}";
        public static string ToggleAdmin(int id) => $"{Base}/{id}";
        public static string DeleteUser(int id) => $"{Base}/{id}";
    }

    public static class F95Link
    {
        public const string Base = $"{Api}/{Authentication}/{F95}";
        public const string Start = $"{Base}/{Endpoints.Start}";
        public static string Check(string f95UserId) => $"{Base}/{Endpoints.Check}/{f95UserId}";
    }

    public static class Tele
    {
        public const string Base = $"{Api}/{Telemetry}";
        public const string Error = $"{Base}/{Endpoints.Error}";

    }

    public static class Mods
    {
        public const string Base = $"{Api}/{Modifications}";
        public const string Upload = $"{Base}/{Endpoints.Upload}";
        public const string Categories = $"{Base}/{Endpoints.Categories}";
        public static string GetVersions(int groupId) => $"{Base}/{Group}/{groupId}/{Versions}";
        public static string Download(int versionId, int fileId) => $"{Base}/{versionId}/{Endpoints.Download}/{fileId}";
        public static string GetMod(int id) => $"{Base}/{id}";
        public static string GetMods(string nameQuerry) => $"{Base}/{nameQuerry}";
        public static string GetMods(int currentPage, int pageSize, string searchTerm, string selectedCategory, SortBy sortBy) =>
            new StringBuilder(Base)
            .Append($"?page={currentPage}")
            .Append($"&pageSize={pageSize}")
            .Append($"&sort={sortBy}")
            .Append($"&search={Uri.EscapeDataString(searchTerm)}")
            .Append(string.IsNullOrWhiteSpace(selectedCategory) ? string.Empty : $"&category={Uri.EscapeDataString(selectedCategory)}")
            .ToString();
    }
}
