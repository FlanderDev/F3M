using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F3M.Server.Services.Import;

/// <summary>
/// What the F95zone import downloads and imports: Import/import-plan.json (shipped with the server), or a replacement
/// an admin uploaded. Built from the community mod list and the hand-made decisions about each post's attachments;
/// import/README.md describes every field.
/// </summary>
public sealed class ImportPlan
{
    /// <summary>Hosts files may come from. Anything else is refused when the plan is loaded.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "attachments.f95zone.to", "gitgud.io", "codeload.github.com",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public List<PlanMod> Mods { get; set; } = [];
    public List<PlanSkipped> Skipped { get; set; } = [];

    /// <summary>Reads and checks a plan. Throws <see cref="InvalidOperationException"/> with every problem found.</summary>
    public static ImportPlan Parse(string json)
    {
        ImportPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ImportPlan>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The plan is not valid JSON: {ex.Message}");
        }

        if (plan is null)
            throw new InvalidOperationException("The plan is empty.");

        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            var shown = string.Join(" ", problems.Take(10));
            throw new InvalidOperationException(problems.Count > 10 ? $"{shown} … and {problems.Count - 10} more problems." : shown);
        }

        return plan;
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (Mods.Count == 0)
            problems.Add("The plan has no mods.");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Mods)
        {
            var label = string.IsNullOrWhiteSpace(mod.Key) ? $"'{mod.Name}'" : mod.Key;
            if (!IsSafeSegment(mod.Key))
                problems.Add($"{label}: the key must be a plain folder name.");
            else if (!keys.Add(mod.Key))
                problems.Add($"{label}: the key is used twice.");
            if (string.IsNullOrWhiteSpace(mod.Name) || string.IsNullOrWhiteSpace(mod.Author) || string.IsNullOrWhiteSpace(mod.Category))
                problems.Add($"{label}: name, author and category are required.");
            if (mod.Description.Length > Configuration.ModDescriptionMaxSize)
                problems.Add($"{label}: the description is longer than {Configuration.ModDescriptionMaxSize} characters.");
            if (mod.F95Owner is { } owner && (owner.UserId.Length == 0 || !owner.UserId.All(char.IsAsciiDigit)))
                problems.Add($"{label}: f95Owner.userId must be a numeric F95zone user id.");
            if (mod.Preview is { } preview && (!IsAllowedUrl(preview) || !Configuration.AllowedThumbnailExtension.Contains(PreviewExtension(preview))))
                problems.Add($"{label}: the preview must be a .jpg, .png or .webp from {string.Join(", ", AllowedHosts)}.");
            if (mod.Versions.Count == 0)
                problems.Add($"{label}: no versions.");
            if (!OptionalFileRules.TryNormalize(mod.OptionalGroups, out _, out var optionalError))
                problems.Add($"{label}: {optionalError}");

            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in mod.Versions)
            {
                if (!IsSafeSegment(version.Version) || !versions.Add(version.Version))
                    problems.Add($"{label}: version '{version.Version}' is missing, used twice or not a plain folder name.");
                if (version.Files.Count == 0)
                    problems.Add($"{label} {version.Version}: no files.");

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in version.Files)
                {
                    var fileLabel = $"{label} {version.Version} '{file.Name}'";
                    if (!IsSafeSegment(file.Name) || !names.Add(file.Name))
                        problems.Add($"{fileLabel}: the name is missing, used twice or not a plain file name.");
                    else if (!Configuration.AllowedFileExtension.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
                        problems.Add($"{fileLabel}: file type not allowed ({string.Join(", ", Configuration.AllowedFileExtension)}).");
                    if (!IsAllowedUrl(file.Url))
                        problems.Add($"{fileLabel}: the url must be https and from {string.Join(", ", AllowedHosts)}.");
                    if (file.Member is { Length: > 0 } && !file.Url.Split('?')[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{fileLabel}: 'member' only works for .zip downloads.");
                    if (!InstallPaths.TryNormalize(file.ArchiveRoot, out _))
                        problems.Add($"{fileLabel}: archiveRoot is not a valid folder.");
                    if (!string.IsNullOrEmpty(file.InstallPath) && !InstallPaths.TryNormalize(file.InstallPath, out _))
                        problems.Add($"{fileLabel}: installPath is not a valid path.");
                    if (!file.IsAuto && !ModTemplates.TryNormalizeId(file.Template, out _))
                        problems.Add($"{fileLabel}: '{file.Template}' is not a mod type.");
                }
            }
        }

        foreach (var mod in Mods)
        {
            foreach (var dependency in mod.Dependencies.Where(d => !keys.Contains(d)))
                problems.Add($"{mod.Key}: depends on '{dependency}', which is not in the plan.");
        }

        if (problems.Count == 0 && FindCycle() is { } cycle)
            problems.Add($"The dependencies form a loop: {cycle}.");

        return problems;
    }

    /// <summary>The mods with every mod before the ones that depend on it.</summary>
    public List<PlanMod> InDependencyOrder()
    {
        var byKey = Mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PlanMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(PlanMod mod)
        {
            if (!seen.Add(mod.Key))
                return;
            foreach (var dependency in mod.Dependencies)
            {
                if (byKey.TryGetValue(dependency, out var other))
                    Visit(other);
            }
            ordered.Add(mod);
        }

        foreach (var mod in Mods)
            Visit(mod);
        return ordered;
    }

    private string? FindCycle()
    {
        var byKey = Mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // false: on the path, true: done
        var path = new List<string>();

        string? Visit(string key)
        {
            if (state.TryGetValue(key, out var done))
                return done ? null : string.Join(" → ", path.SkipWhile(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase)).Append(key));
            state[key] = false;
            path.Add(key);
            foreach (var dependency in byKey[key].Dependencies)
            {
                if (Visit(dependency) is { } cycle)
                    return cycle;
            }
            path.RemoveAt(path.Count - 1);
            state[key] = true;
            return null;
        }

        return Mods.Select(m => Visit(m.Key)).FirstOrDefault(c => c is not null);
    }

    public static string PreviewExtension(string url) =>
        Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();

    private static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host);

    /// <summary>One file or folder name: the downloads are stored under the mod's key, version and file name.</summary>
    private static bool IsSafeSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 120
        && value is not ("." or "..")
        && value == value.Trim()
        && !value.EndsWith('.')
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
}

public sealed class PlanMod
{
    /// <summary>Stable id, also the folder the downloads go in.</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public DateTime? PostedAt { get; set; }

    /// <summary>Who posted the files on F95zone: the mod goes to their account.</summary>
    public PlanOwner? F95Owner { get; set; }

    /// <summary>URL of a preview image.</summary>
    public string? Preview { get; set; }

    /// <summary>Keys of the mods this one needs.</summary>
    public List<string> Dependencies { get; set; } = [];

    /// <summary>What was left out of the mod, and why.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Parts of every version the mod does not need, such as example missions. Readmes and examples are also found
    /// automatically on import; groups named here come first.
    /// </summary>
    public List<OptionalGroupDto> OptionalGroups { get; set; } = [];

    /// <summary>Oldest first.</summary>
    public List<PlanVersion> Versions { get; set; } = [];
}

public sealed class PlanOwner
{
    public string UserId { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public sealed class PlanVersion
{
    public string Version { get; set; } = string.Empty;
    public DateTime? UploadedAt { get; set; }
    public List<PlanFile> Files { get; set; } = [];
}

public sealed class PlanFile
{
    public string Url { get; set; } = string.Empty;

    /// <summary>The name the file is stored and offered under.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>For a .zip download: the one file inside it to keep, stored as <see cref="Name"/> instead of the archive.</summary>
    public string? Member { get; set; }

    /// <summary>Archives: only this folder is installed, without the folder itself.</summary>
    public string? ArchiveRoot { get; set; }

    /// <summary>Empty: the server works it out from the archive (with <see cref="Template"/> auto).</summary>
    public string? InstallPath { get; set; }

    /// <summary>A <see cref="ModTemplates"/> id, or "auto" to let the server recognise it.</summary>
    public string Template { get; set; } = UploadFile.AutoTemplate;

    [JsonIgnore]
    public bool IsAuto => string.Equals(Template, UploadFile.AutoTemplate, StringComparison.OrdinalIgnoreCase);
}

public sealed class PlanSkipped
{
    public string Key { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string Reason { get; set; } = string.Empty;
}
