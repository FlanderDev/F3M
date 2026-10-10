using System.Security.Cryptography;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using SharpCompress.Archives;

namespace F3M.Server.Services;

/// <summary>One file the upload will place in the game folder.</summary>
public sealed record InspectedEntry(string From, string To, long Size, string Sha256, PlacementKind Kind);

/// <summary>Everything the server derives from one uploaded file.</summary>
public sealed record InspectionResult(
    string Sha256,
    long Size,
    bool IsArchive,
    string TargetPath,
    IReadOnlyList<InspectedEntry> Entries);

/// <summary>
/// Reads an uploaded file once and derives what the catalog needs: its hash, where it goes, and, for archives,
/// every file inside with its own target and hash. Failures throw <see cref="InvalidDataException"/> with a message
/// that is safe to show the uploader.
/// </summary>
public static class ModFileInspector
{
    public const int MaxEntries = 20_000;
    public const int MaxFromLength = 512;

    /// <summary>Caps the total uncompressed size of one archive, to stop zip bombs.</summary>
    public const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    public static InspectionResult Inspect(string diskPath, string originalName, string? installPath)
    {
        if (!InstallPaths.TryPlan(originalName, installPath, out var placement))
        {
            throw new InvalidDataException(
                $"The install path for '{originalName}' is not valid. Use a path relative to the game folder, e.g. BepInEx/plugins/MyMod.");
        }

        var (sha256, size) = HashFile(diskPath);

        if (!placement.IsArchive)
        {
            var entry = new InspectedEntry(originalName, placement.Target, size, sha256, ClassifyKind(placement.Target));
            return new InspectionResult(sha256, size, false, placement.Target, [entry]);
        }

        var entries = ReadArchive(diskPath, placement.Target);
        return new InspectionResult(sha256, size, true, placement.Target, entries);
    }

    public static PlacementKind ClassifyKind(string target)
    {
        var extension = Path.GetExtension(target);

        if (target.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)
            && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return PlacementKind.Plugin;
        }

        if (target.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)
            && extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
        {
            return PlacementKind.Config;
        }

        return PlacementKind.Other;
    }

    private static (string Sha256, long Size) HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static List<InspectedEntry> ReadArchive(string path, string targetFolder)
    {
        using var stream = File.OpenRead(path);

        IArchive archive;
        try
        {
            archive = ArchiveFactory.OpenArchive(stream);
        }
        catch (Exception)
        {
            throw new InvalidDataException("The archive could not be read. It may be corrupt, encrypted, or in an unsupported format.");
        }

        using (archive)
        {
            var entries = new List<InspectedEntry>();
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extracted = 0;

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                    continue;

                if (entries.Count >= MaxEntries)
                    throw new InvalidDataException($"The archive contains more than {MaxEntries:N0} files.");

                if (!InstallPaths.TrySplit(entry.Key, out var parts, out _) || parts.Count == 0)
                    throw new InvalidDataException($"The archive contains an invalid path: '{entry.Key}'.");

                var from = string.Join('/', parts);
                if (from.Length > MaxFromLength)
                    throw new InvalidDataException($"The archive contains a path that is too long: '{from[..80]}…'.");

                if (!InstallPaths.TryExpand(targetFolder, from, out var to) || to.Length > InstallPaths.MaxLength)
                    throw new InvalidDataException($"The archive contains a file with an invalid or too long install path: '{from}'.");

                if (!seenTargets.Add(to))
                    throw new InvalidDataException($"The archive contains '{to}' more than once.");

                var (sha256, size) = HashEntry(entry);
                extracted += size;
                if (extracted > MaxExtractedBytes)
                    throw new InvalidDataException("The archive expands to more than 2 GB.");

                entries.Add(new InspectedEntry(from, to, size, sha256, ClassifyKind(to)));
            }

            if (entries.Count == 0)
                throw new InvalidDataException("The archive contains no files.");

            return entries;
        }
    }

    /// <summary>Streams one archive entry through SHA-256, counting the bytes actually read rather than trusting the header.</summary>
    private static (string Sha256, long Size) HashEntry(IArchiveEntry entry)
    {
        using var source = entry.OpenEntryStream();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            total += read;
        }

        return (Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), total);
    }
}
