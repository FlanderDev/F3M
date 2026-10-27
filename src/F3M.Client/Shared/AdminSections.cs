namespace F3M.Client.Shared;

/// <summary>One page of the admin area: an entry in its sidebar and a tile on its overview.</summary>
public sealed record AdminSection(string Group, string Title, string Icon, string Href, string Summary);

/// <summary>The admin pages, in the order the sidebar lists them. Add a page here to give it a sidebar entry.</summary>
public static class AdminSections
{
    public const string OverviewHref = "/admin";

    public static readonly IReadOnlyList<AdminSection> All =
    [
        new("Site", "Statistics", "bi-graph-up", "/admin/stats",
            "Downloads, visitors, searches, accounts, uploads and problems over time."),
        new("Site", "Log", "bi-journal-text", "/admin/log",
            "Sign-ins, uploads, admin actions, and warnings and errors from the server and visitors' browsers."),
        new("Content", "Users", "bi-people-fill", "/admin/users",
            "Accounts, their F95zone profiles and roles; make admins or delete accounts."),
        new("Content", "Tags", "bi-tags-fill", "/admin/tags",
            "The tags uploaders pick and visitors filter by, and which tags include which."),
        new("Content", "Mod catalog", "bi-journal-check", "/admin/catalog",
            "The signed list of mods the desktop app reads; rebuild it after restores or manual changes."),
        new("Content", "F95zone import", "bi-cloud-download-fill", "/admin/import",
            "Imports the community mods from F95zone as unclaimed mods for their uploaders."),
        new("Server", "Site setup", "bi-ui-checks", "/admin/setup",
            "What a new site still needs: the F95zone bot account and the frameworks."),
        new("Server", "Server update", "bi-cloud-arrow-up-fill", "/admin/server-update",
            "The running version, new commits, and updating the server from here."),
    ];
}
