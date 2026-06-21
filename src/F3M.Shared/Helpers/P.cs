namespace F3M.Shared.Helpers;

/// <summary>
/// Contains page routes, to avoid magic strings in controllers and client code.
/// </summary>
public static class P
{
    #region Parts of paths
    public const string Upload = "upload";
    public const string Modification = "mod";
    public const string Edit = "edit";
    #endregion

    public static class Uploads
    {
        public const string Base = $"/{Upload}";
        public static string ModVersion(int modGroupId) => $"{Base}/{modGroupId}";
    }

    public static class Mods
    {
        public const string Base = $"/{Modification}";
        public static string Edit(int selectedVersionId) => $"{Base}/{selectedVersionId}/{P.Edit}";
    }
}
