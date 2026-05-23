namespace F3M.Shared.Helpers;

/// <summary>
/// Contains all Pages routes, to avoid magic strings in controllers and client code.
/// </summary>
/// <remarks>
/// Really want to avoid magic values.
/// Page directives are an exception.
/// </remarks>
public static class P
{
    #region Parts of paths
    public const string Upload = "upload";
    public const string Modifications = "mods";
    public const string Edit = "edit";
    #endregion

    public static class Uploads
    {
        public const string Base = $"/{Upload}";
        public static string ModVersion(int modGroupId) => $"{Upload}/{modGroupId}";
    }

    public static class Mods
    {
        public const string Base = $"/{Modifications}";
        public static string Edit(int selectedVersionId) => $"{Modifications}/{selectedVersionId}/{P.Edit}";
    }
}
