using F3M.Client.Services;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Client.Models;

/// <summary>
/// Represents a single mod file selected by the user before upload.
/// </summary>
/// <remarks>
/// Must be a class (not a record) so InstallPath mutations via @oninput
/// are reflected on the same heap object.
/// </remarks>
public sealed class FileEntry
{
    public required string OriginalName { get; init; }
    public required long Size { get; init; }
    public required byte[] Bytes { get; init; }

    /// <summary>What is inside an archive, read once when the file is picked; null for plain files.</summary>
    public ArchiveListing? Listing { get; init; }

    public string InstallPath { get; set; } = string.Empty;

    /// <summary>Archives only: the folder inside the archive to install ("" = all of it).</summary>
    public string ArchiveRoot { get; set; } = string.Empty;

    /// <summary>The file's mod type, a <see cref="ModTemplates"/> id.</summary>
    public string Template { get; set; } = ModTemplates.Custom;

    /// <summary>What the form recognised when the file was picked (<see cref="ModTemplates.Suggest"/>).</summary>
    public TemplateSuggestion? Suggestion { get; init; }

    /// <summary>The folders inside an archive, for picking <see cref="ArchiveRoot"/>.</summary>
    public IReadOnlyList<string> ArchiveFolders => _folders ??= Listing is null
        ? []
        : [.. Listing.Entries
            .SelectMany(e => Prefixes(e.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaxArchiveFolders)];

    private const int MaxArchiveFolders = 300;
    private IReadOnlyList<string>? _folders;

    private static IEnumerable<string> Prefixes(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join('/', parts.Take(i));
    }

    /// <summary>The author typed or picked an install path; picking another type then leaves it alone.</summary>
    public bool InstallPathEdited { get; set; }

    // The targets and warnings below cost a pass over the archive listing: computed once per input.
    private string _cacheKey = "\0";
    private List<string>? _targets;
    private IReadOnlyList<string> _warnings = [];
    private string? _warningsKey;

    /// <summary>The game paths this file installs; null when the install path or archive folder is not valid.</summary>
    public List<string>? Targets
    {
        get
        {
            Refresh();
            return _targets;
        }
    }

    /// <summary>
    /// What does not look right for the file's type (<see cref="ModTemplates.Check"/>), looking only at the files that
    /// are installed: those in an optional group are not.
    /// </summary>
    public IReadOnlyList<string> TypeWarningsWith(IReadOnlyList<OptionalGroupDto> optionalGroups)
    {
        Refresh();
        if (_targets is not { Count: > 0 } targets)
            return [];

        var key = string.Join('\n', optionalGroups.Select(g => $"{g.Name}:{string.Join('|', g.Patterns)}"));
        if (key != _warningsKey)
        {
            _warningsKey = key;
            _warnings = ModTemplates.Check(Template, OptionalFileRules.Installed(optionalGroups, targets));
        }

        return _warnings;
    }

    private void Refresh()
    {
        var key = $"{InstallPath}\n{ArchiveRoot}\n{Template}";
        if (key == _cacheKey)
            return;

        _cacheKey = key;
        var paths = Listing?.Entries.Select(e => e.RelativePath).ToList();
        _targets = ModTemplates.TargetsOf(OriginalName, paths, InstallPath, ArchiveRoot);
        _warningsKey = null;
    }
}
