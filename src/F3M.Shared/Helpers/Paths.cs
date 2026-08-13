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
    public const string LinkAccount = $"/auth/link-account";
    #endregion

    public static class Mods
    {
        public const string Base = $"/{Modification}";
        public const string Upload = $"/{Modification}/{Paths.Upload}";
        public static string Edit(int selectedVersionId) => $"{Base}/{selectedVersionId}/{Paths.Edit}";
        public static string UploadVersion(int modGroupId) => $"{Upload}/{modGroupId}";

    }
}
