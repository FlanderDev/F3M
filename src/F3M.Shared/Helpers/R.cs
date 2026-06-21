using System.Text;

namespace F3M.Shared.Helpers;

/// <summary>
/// Contains all API routes, to avoid magic strings in controllers and client code.
/// </summary>
public static class R
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
    public const string Login = "login";
    public const string Register = "register";
    public const string Authentication = "auth";
    #endregion

    public static class Admin
    {
        public const string Base = $"{Api}/{Administrator}";
        public const string GetUsers = $"{Base}/{Users}";
        public static string ToggleAdmin(int id) => $"{Base}/{id}";
        public static string DeleteUser(int id) => $"{Base}/{id}";
    }

    public static class Auth
    {
        public const string Base = $"{Api}/{Authentication}";
        public const string Register = $"{Base}/{R.Register}";
        public const string Login = $"{Base}/{R.Login}";
    }

    public static class Mods
    {
        public const string Base = $"{Api}/{Modifications}";
        public const string Upload = $"{Base}/{R.Upload}";
        public const string Categories = $"{Base}/{R.Categories}";
        public static string GetVersions(int groupId) => $"{Base}/{Group}/{groupId}/{Versions}";
        public static string Download(int versionId, int fileId) => $"{Base}/{versionId}/{R.Download}/{fileId}";
        public static string GetMod(int id) => $"{Base}/{id}";
        public static string GetMods(string nameQuerry) => $"{Base}/{nameQuerry}";
        public static string GetMods(int currentPage, int pageSize, string searchTerm, string selectedCategory, Configuration.SortBy sortBy) =>
            new StringBuilder(Base)
            .Append($"?page={currentPage}")
            .Append($"&pageSize={pageSize}")
            .Append($"&sort={sortBy}")
            .Append($"&search={Uri.EscapeDataString(searchTerm)}")
            .Append(string.IsNullOrWhiteSpace(selectedCategory) ? string.Empty : $"&category={Uri.EscapeDataString(selectedCategory)}")
            .ToString();
    }
}
