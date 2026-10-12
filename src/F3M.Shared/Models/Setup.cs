namespace F3M.Shared.Models;

/// <summary>How far one step of setting up the site is.</summary>
public enum SetupCheckState
{
    /// <summary>Nothing to do.</summary>
    Done,

    /// <summary>Not done yet; the site works without it, with less.</summary>
    Todo,

    /// <summary>Set up, but broken.</summary>
    Problem,
}

/// <summary>One line of the setup checklist.</summary>
public class SetupCheckDto
{
    public string Title { get; set; } = string.Empty;
    public SetupCheckState State { get; set; }
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Everything the setup page shows.</summary>
public class SetupStatusDto
{
    public List<SetupCheckDto> Checks { get; set; } = [];
    public BotSettingsDto Bot { get; set; } = new();
    public List<SetupFrameworkDto> Frameworks { get; set; } = [];
}

/// <summary>A mod that a mod type needs (<see cref="Helpers.ModTemplate.RequiredModName"/>), and whether the site has it.</summary>
public class SetupFrameworkDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The mod types that need it, e.g. "Custom Missions v2".</summary>
    public List<string> NeededBy { get; set; } = [];

    /// <summary>The newest version on the site; null while the site does not have the mod.</summary>
    public int? ModId { get; set; }

    /// <summary>True when the import plan can import it.</summary>
    public bool InPlan { get; set; }
}

/// <summary>Where the F95zone bot account in use comes from.</summary>
public enum BotSettingsSource
{
    /// <summary>Neither the admin page nor the F95Bot__* settings name an account: account linking is off.</summary>
    None,

    /// <summary>The F95Bot__* environment variables (or appsettings).</summary>
    Environment,

    /// <summary>Saved on the setup page; overrides the environment variables.</summary>
    Site,
}

/// <summary>The F95zone bot account that verifies account links. The password never leaves the server.</summary>
public class BotSettingsDto
{
    public BotSettingsSource Source { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public bool HasPassword { get; set; }

    /// <summary>True when the environment variables name an account too, so removing the saved one falls back to it.</summary>
    public bool EnvironmentConfigured { get; set; }

    /// <summary>The last "Test login" since the server started; null when none ran.</summary>
    public BotTestResultDto? LastTest { get; set; }
}

/// <summary>Saves the bot account on the site.</summary>
public class BotSettingsUpdateDto
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Null or empty keeps the saved password.</summary>
    public string? Password { get; set; }

    public string UserId { get; set; } = string.Empty;
}

public class BotTestResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime At { get; set; }
}
