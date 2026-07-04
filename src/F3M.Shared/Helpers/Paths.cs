namespace F3M.Shared.Helpers;

/// <summary>
/// Contains page routes, to avoid magic strings in controllers and client code.
/// </summary>
public static class Paths
{
    #region Parts of paths
    public const string Authentication = "auth";
    public const string Upload = "upload";
    public const string Modification = "mod";
    public const string Edit = "edit";

    #endregion

    public static class Auth
    {
        public const string Base = $"/{Authentication}/";
        public const string Login = $"/{Authentication}/login";
        public const string Register = $"/{Authentication}/register";

    }
    public static class Mods
    {
        public const string Base = $"/{Modification}";
        public const string Upload = $"/{Modification}/{Paths.Upload}";
        public static string Edit(int selectedVersionId) => $"{Base}/{selectedVersionId}/{Paths.Edit}";
        public static string UploadVersion(int modGroupId) => $"{Upload}/{modGroupId}";

    }
}
