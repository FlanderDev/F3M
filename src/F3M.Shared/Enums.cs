namespace F3M.Shared;

public enum SortBy
{
    Newest,
    Oldest,
    DownloadsAsc,
    DownloadsDesc,
    NameAsc,
    NameDesc
}

public enum VerificationState
{
    None,
    Verified,
    Pending,
    Expired,
    Error,
    NotFound
}