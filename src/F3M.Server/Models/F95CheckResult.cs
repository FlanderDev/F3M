namespace F3M.Server.Models;

/// <summary>
/// One look at an F95zone profile while someone links their account. F95zone blocks bot traffic now and then; these
/// tell how often the checker got through lately, for the status on the link page. Kept for a month.
/// </summary>
public class F95CheckResult
{
    public int Id { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public F95CheckOutcome Outcome { get; set; }
}

public enum F95CheckOutcome
{
    /// <summary>The profile could be read (whether the code was on it or not).</summary>
    Reached,

    /// <summary>The profile could be read, the code was on it, and the account is linked.</summary>
    Linked,

    /// <summary>F95zone showed the page signed out, even after the bot signed in again.</summary>
    SignedOut,

    /// <summary>F95zone couldn't be reached or refused the page.</summary>
    Failed,
}
