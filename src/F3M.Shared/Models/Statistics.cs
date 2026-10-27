namespace F3M.Shared.Models;

/// <summary>Everything the statistics page shows, for the last <see cref="Days"/> days and of all time.</summary>
public sealed class SiteStats
{
    public int Days { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }

    /// <summary>
    /// The first day with counted downloads, visits, searches, sign-ins and desktop syncs; null before anything was
    /// counted. Those numbers only exist from then on, so periods before it can't be compared.
    /// </summary>
    public DateOnly? CountingSince { get; set; }

    public StatsTotals Totals { get; set; } = new();
    public StatsPeriod Period { get; set; } = new();

    /// <summary>The same numbers for the <see cref="Days"/> days before <see cref="From"/>, for the change.</summary>
    public StatsPeriod PreviousPeriod { get; set; } = new();

    public List<DailyDownloads> Downloads { get; set; } = [];
    public List<DailyVisits> Visits { get; set; } = [];
    public List<DailySignIns> SignIns { get; set; } = [];
    public List<DailyProblems> Problems { get; set; } = [];

    /// <summary>The last 12 months, oldest first, including the current one.</summary>
    public List<MonthlyGrowth> Growth { get; set; } = [];

    /// <summary>Per mod type (type tag); a mod of several types counts for each, one without for "(no type)".</summary>
    public List<CategoryStats> Categories { get; set; } = [];
    public List<TopMod> TopModsAllTime { get; set; } = [];
    public List<TopMod> TopModsInPeriod { get; set; } = [];

    public List<NamedCount> Searches { get; set; } = [];
    public List<NamedCount> SearchesWithoutResults { get; set; } = [];

    /// <summary>The tags people filtered the browse page by (names as filtered, in lower case).</summary>
    public List<NamedCount> TagFilters { get; set; } = [];
    public List<NamedCount> DesktopVersions { get; set; } = [];

    /// <summary>The tags people suggested for mods, and the new tags they asked for (names in lower case).</summary>
    public List<NamedCount> TagsSuggested { get; set; } = [];
    public List<NamedCount> TagsRequested { get; set; } = [];

    /// <summary>The last 12 weeks, oldest first, including the current one.</summary>
    public List<WeeklyTagSuggestions> TagSuggestions { get; set; } = [];

    public VerificationStats Verifications { get; set; } = new();
}

/// <summary>How things stand now.</summary>
public sealed class StatsTotals
{
    /// <summary>Published mods (one per mod, whatever its number of versions).</summary>
    public int Mods { get; set; }
    public int Versions { get; set; }
    public int Files { get; set; }
    public long StorageBytes { get; set; }
    public long Downloads { get; set; }
    public int Users { get; set; }

    /// <summary>Accounts linked to an F95zone profile.</summary>
    public int LinkedUsers { get; set; }

    /// <summary>Imported mods whose F95zone uploader hasn't signed in yet.</summary>
    public int UnclaimedMods { get; set; }
}

/// <summary>Sums over a period.</summary>
public sealed class StatsPeriod
{
    public long Downloads { get; set; }

    /// <summary>Different browsers that opened the site (counted once per period).</summary>
    public int Visitors { get; set; }
    public long PageLoads { get; set; }
    public int NewUsers { get; set; }
    public int NewMods { get; set; }
    public int NewVersions { get; set; }
    public long DesktopSyncs { get; set; }
    public long SignIns { get; set; }
}

public sealed class DailyDownloads
{
    public DateOnly Day { get; set; }
    public long Browser { get; set; }
    public long Desktop { get; set; }
}

public sealed class DailyVisits
{
    public DateOnly Day { get; set; }
    public int Visitors { get; set; }
    public long PageLoads { get; set; }
}

public sealed class DailySignIns
{
    public DateOnly Day { get; set; }
    public long Password { get; set; }

    /// <summary>Sign-ins (and new accounts) through an F95zone verification.</summary>
    public long F95 { get; set; }
}

/// <summary>Warnings and errors in the site log on one day.</summary>
public sealed class DailyProblems
{
    public DateOnly Day { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }
}

public sealed class MonthlyGrowth
{
    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; set; }
    public int NewUsers { get; set; }
    public int NewMods { get; set; }

    /// <summary>Versions added to mods that already existed.</summary>
    public int NewVersions { get; set; }
}

public sealed class WeeklyTagSuggestions
{
    /// <summary>The Monday the week starts on.</summary>
    public DateOnly Week { get; set; }

    /// <summary>Suggestions of existing tags.</summary>
    public int Suggested { get; set; }

    /// <summary>Requests for new tags.</summary>
    public int Requested { get; set; }

    /// <summary>Suggestions and requests approved or rejected.</summary>
    public int Decided { get; set; }
}

public sealed class CategoryStats
{
    public string Name { get; set; } = string.Empty;
    public int Mods { get; set; }
    public long Downloads { get; set; }
    public long StorageBytes { get; set; }
}

public sealed class TopMod
{
    public int GroupId { get; set; }

    /// <summary>The latest version, for the link.</summary>
    public int ModId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public long Downloads { get; set; }
}

public sealed class NamedCount
{
    public string Name { get; set; } = string.Empty;
    public long Count { get; set; }
}

/// <summary>Account-link verifications started in the period, by how they ended.</summary>
public sealed class VerificationStats
{
    public int Verified { get; set; }

    /// <summary>Not finished within the 24 hours a code is valid.</summary>
    public int Expired { get; set; }

    /// <summary>Replaced by another verification, or refused because the profile didn't match.</summary>
    public int Cancelled { get; set; }

    /// <summary>Still waiting for the code to be posted.</summary>
    public int Pending { get; set; }
}

/// <summary>Sent by the website once per page load.</summary>
public sealed class VisitReport
{
    /// <summary>A random id the browser keeps, so a visitor counts once per day; null when the browser asks not to be tracked.</summary>
    public string? VisitorId { get; set; }
}

/// <summary>Sent by the browse page when a search has stayed on screen for a moment (not for every key pressed).</summary>
public sealed class SearchReport
{
    public string Term { get; set; } = string.Empty;
    public int Results { get; set; }
}
