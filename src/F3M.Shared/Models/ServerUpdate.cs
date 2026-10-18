namespace F3M.Shared.Models;

/// <summary>
/// The server's version and the state of updates started from the admin page. The updates themselves run on the host,
/// through the service that admin-updates.sh installs; the server only leaves requests for it and reads its reports.
/// </summary>
public sealed class ServerUpdateStatus
{
    /// <summary>When the running server was built (the date the header shows too).</summary>
    public string? RunningVersion { get; set; }

    /// <summary>The git commit the running server was built from (Docker builds); null for a build without it (e.g. from an IDE).</summary>
    public string? RunningCommit { get; set; }

    /// <summary>Whether the host service is installed: it has reported at least once.</summary>
    public bool HelperInstalled { get; set; }

    public DateTime? CheckedAt { get; set; }
    public string? Branch { get; set; }
    public string? Upstream { get; set; }

    /// <summary>The commit checked out on the host. Differs from <see cref="RunningCommit"/> when a rebuild failed.</summary>
    public string? HeadCommit { get; set; }

    /// <summary>Why the last check for new commits failed, if it did.</summary>
    public string? CheckError { get; set; }

    /// <summary>Commits on the upstream branch that the host hasn't got yet, newest first.</summary>
    public List<PendingCommit> NewCommits { get; set; } = [];

    /// <summary>An update was requested and the host hasn't started it yet.</summary>
    public bool UpdateRequested { get; set; }

    /// <summary>A check for new commits was requested and the host hasn't done it yet.</summary>
    public bool CheckRequested { get; set; }

    public ServerUpdateState State { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>The end of the last update's output.</summary>
    public string? Log { get; set; }
}

public enum ServerUpdateState
{
    /// <summary>No update was run from the admin page yet.</summary>
    None,
    Running,
    Succeeded,
    Failed
}

public sealed class PendingCommit
{
    public string Sha { get; set; } = string.Empty;
    public DateTimeOffset? Date { get; set; }
    public string Subject { get; set; } = string.Empty;
}
