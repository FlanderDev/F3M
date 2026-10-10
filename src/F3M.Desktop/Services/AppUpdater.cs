using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using F3M.Desktop.Core;
using Velopack;
using Velopack.Sources;

namespace F3M.Desktop.Services;

/// <summary>A downloadable AppImage from a GitHub release. <see cref="Sha256"/> is GitHub's digest, when it gives one.</summary>
public sealed record AppImageAsset(string Url, long Size, string? Sha256);

/// <summary>
/// A newer F3M Desktop. <see cref="Velopack"/> is set for a Velopack install and <see cref="AppImage"/> for an AppImage;
/// with neither, this copy cannot update itself.
/// </summary>
public sealed record AppUpdate(string Version, UpdateInfo? Velopack, AppImageAsset? AppImage)
{
    public bool CanInstall => Velopack is not null || AppImage is not null;
}

/// <summary>
/// Updates F3M Desktop itself from the GitHub releases.
/// <list type="bullet">
/// <item>An install made by the Velopack setup (Windows) downloads the update and restarts into it through Velopack.</item>
/// <item>An AppImage (Linux) downloads the new AppImage next to itself, swaps it in and starts it.</item>
/// <item>Anything else (a portable copy, a development build) is only told that a newer version exists.</item>
/// </list>
/// </summary>
public sealed partial class AppUpdater(AppServices app)
{
    private UpdateManager? _manager;

    private UpdateManager Manager =>
        _manager ??= new UpdateManager(new GithubSource(F3M.Shared.Configuration.RepositoryUrl, null, false));

    /// <summary>Whether this copy was installed by the Velopack setup, so Velopack can replace it.</summary>
    public bool IsVelopackInstall
    {
        get
        {
            try
            {
                return Manager.IsInstalled;
            }
            catch (Exception ex)
            {
                AppLog.Error("Velopack could not tell whether the app is installed", ex);
                return false;
            }
        }
    }

    /// <summary>The AppImage file this process runs from, or null when it is not an AppImage.</summary>
    public static string? AppImagePath =>
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

    public string CurrentVersion =>
        IsVelopackInstall
            ? Manager.CurrentVersion!.ToString()
            : typeof(AppUpdater).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

    /// <summary>The newer version, or null when this is the newest.</summary>
    public async Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        if (IsVelopackInstall)
        {
            var info = await Manager.CheckForUpdatesAsync().WaitAsync(ct);
            return info is null ? null : new AppUpdate(info.TargetFullRelease.Version.ToString(), info, null);
        }

        // Not a Velopack install: read the newest release's assets, whose names carry the version.
        var release = await LatestReleaseAsync(ct);
        if (release is null || !Version.TryParse(CurrentVersion, out var current)) return null;

        if (AppImagePath is not null)
        {
            var suffix = $"-{AppImageArch()}.AppImage";
            var image = release.Assets
                .Where(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(a => (Asset: a, Version: VersionOf(a.Name)))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (image.Version is null || image.Version <= current) return null;

            var sha = image.Asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? image.Asset.Digest["sha256:".Length..].ToLowerInvariant()
                : null;
            return new AppUpdate(image.Version.ToString(),
                null, new AppImageAsset(image.Asset.DownloadUrl, image.Asset.Size, sha));
        }

        var newest = release.Assets.Select(a => VersionOf(a.Name)).Where(v => v is not null).DefaultIfEmpty().Max();
        return newest is null || newest <= current ? null : new AppUpdate(newest.ToString(), null, null);
    }

    /// <summary>Downloads the update. Progress is 0 to 100.</summary>
    public async Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct)
    {
        if (update.Velopack is not null)
        {
            await Manager.DownloadUpdatesAsync(update.Velopack, progress, ct);
            return;
        }

        if (update.AppImage is not null)
        {
            await DownloadAppImageAsync(update.AppImage, progress, ct);
            return;
        }

        throw new UserException("This copy of F3M Desktop cannot update itself.");
    }

    /// <summary>
    /// Installs the downloaded update and starts the new version. A Velopack update ends this process right away; an
    /// AppImage is swapped in and the app restarts from the same path.
    /// </summary>
    public void ApplyAndRestart(AppUpdate update)
    {
        if (update.Velopack is not null)
        {
            app.PrepareForExit();
            Manager.ApplyUpdatesAndRestart(update.Velopack.TargetFullRelease);
            return;
        }

        var target = AppImagePath ?? throw new UserException("F3M Desktop is no longer running from an AppImage.");
        // Renaming over the running file is safe on Linux: this process keeps the old file open until it exits.
        File.Move(PendingAppImage(target), target, overwrite: true);
        AppLog.Info($"Replaced {target} with version {update.Version}; restarting");
        app.Restart();
    }

    private static string PendingAppImage(string appImage) => appImage + ".update";

    /// <summary>
    /// Downloads into a file next to the AppImage, so the final swap is a rename on the same disk. The file is checked
    /// against its size and, when GitHub gives one, its SHA-256 digest before it is made executable.
    /// </summary>
    private async Task DownloadAppImageAsync(AppImageAsset asset, Action<int> progress, CancellationToken ct)
    {
        var target = AppImagePath ?? throw new UserException("F3M Desktop is not running from an AppImage.");
        var pending = PendingAppImage(target);
        try
        {
            using var response = await app.Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress((int)(done * 100 / total));
                }
            }

            if (asset.Size > 0 && new FileInfo(pending).Length != asset.Size)
                throw new UserException("The downloaded update is incomplete. Try again.");
            if (asset.Sha256 is not null && await FileOps.Sha256FileAsync(pending, ct) != asset.Sha256)
                throw new UserException("The downloaded update failed its checksum. Try again.");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(pending, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(pending);
            throw new UserException($"The folder of {Path.GetFileName(target)} can't be written to, so the update can't be " +
                                    "saved next to it. Move the AppImage to a folder you own, or download the update by hand.");
        }
        catch
        {
            TryDelete(pending);
            throw;
        }
    }

    private async Task<GithubRelease?> LatestReleaseAsync(CancellationToken ct)
    {
        var repo = new Uri(F3M.Shared.Configuration.RepositoryUrl).AbsolutePath.Trim('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await app.Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GithubRelease>(timeout.Token);
    }

    private static Version? VersionOf(string assetName)
    {
        var match = AssetVersionPattern().Match(assetName);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>The architecture name in AppImage file names.</summary>
    private static string AppImageArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "aarch64",
        _ => "x86_64",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not remove {path}", ex);
        }
    }

    /// <summary>"F3M.Desktop-0.1.42-x86_64.AppImage", "F3M.Desktop-0.1.42-full.nupkg" and the like.</summary>
    [GeneratedRegex(@"^F3M\.Desktop-(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AssetVersionPattern();

    private sealed class GithubRelease
    {
        [JsonPropertyName("assets")]
        public List<GithubAsset> Assets { get; set; } = [];
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>"sha256:…", given by GitHub for assets uploaded since mid 2025.</summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
