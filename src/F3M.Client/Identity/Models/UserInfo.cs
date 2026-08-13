namespace F3M.Client.Identity.Models;

/// <summary>
/// User info from identity endpoint to establish claims.
/// </summary>
public class UserInfo
{
    /// <summary>
    /// The username. Always present — this is what F95-linked (email-less) accounts
    /// are identified by.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// The email address. Empty for F95-linked accounts, which don't collect one.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// A value indicating whether the email has been confirmed yet.
    /// </summary>
    public bool IsEmailConfirmed { get; set; }
}
