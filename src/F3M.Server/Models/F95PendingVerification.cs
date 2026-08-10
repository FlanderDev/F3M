namespace F3M.Server.Models;

/// <summary>
/// Tracks an in-progress F95zone account verification challenge.
/// One row per pending flow; cancelled/expired rows are kept for audit.
/// </summary>
public class F95PendingVerification
{
    public int Id { get; set; }

    /// <summary>Numeric F95zone user ID parsed from the profile URL.</summary>
    public string F95UserId { get; set; } = string.Empty;

    /// <summary>F95zone username parsed from the profile URL.</summary>
    public string F95Username { get; set; } = string.Empty;

    /// <summary>The GUID the user must reply with to complete verification.</summary>
    public string VerificationGuid { get; set; } = string.Empty;

    /// <summary>
    /// The ID of the user's own profile-wall post that matched the verification GUID.
    /// Populated only once verification succeeds (0 until then) — kept for audit purposes.
    /// </summary>
    public long ProfilePostId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public F95VerificationStatus Status { get; set; } = F95VerificationStatus.Pending;
}

public enum F95VerificationStatus
{
    Pending,
    Verified,
    Expired,
    Cancelled
}
