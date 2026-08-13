namespace F3M.Shared.Helpers;

/// <summary>
/// Contains page routes, to avoid magic strings in controllers and client code.
/// </summary>
public static class Paths
{
    #region Parts of paths
    public const string Upload = "upload";
    public const string Modification = "mod";
    public const string Edit = "edit";
    public const string LinkAccount = $"link-account";
    public const string Authentication = $"auth";
    public const string Profile = "/profile";
    #endregion

    public static class Mods
    {
        public const string Base = $"/{Modification}";
        public const string Upload = $"{Base}/{Paths.Upload}";
        public static string Edit(int selectedVersionId) => $"{Base}/{selectedVersionId}/{Paths.Edit}";
        public static string UploadVersion(int modGroupId) => $"{Upload}/{modGroupId}";
    }

    public static class Auth
    {
        public const string Base = $"/{Authentication}";
        public const string LinkAccount = $"{Base}/{Paths.LinkAccount}";
    }
}
