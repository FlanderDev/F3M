using SharpCompress.Archives;

namespace F3M.Client.Services;

public sealed record ArchiveEntryInfo(string RelativePath, long Size);

/// <param name="Truncated">More files than <see cref="ArchiveInspector.MaxEntries"/>; the rest are not listed.</param>
/// <param name="Error">Set when the archive could not be read (corrupt, encrypted, unsupported).</param>
public sealed record ArchiveListing(IReadOnlyList<ArchiveEntryInfo> Entries, bool Truncated, string? Error);

/// <summary>Lists what is inside a zip/7z/rar without extracting it. Only reads the archive's index.</summary>
public static class ArchiveInspector
{
    public const int MaxEntries = 20_000;

    public static ArchiveListing List(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = ArchiveFactory.OpenArchive(stream);

            var entries = new List<ArchiveEntryInfo>();
            var truncated = false;
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                    continue;

                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                entries.Add(new ArchiveEntryInfo(entry.Key, entry.Size));
            }

            return new ArchiveListing(entries, truncated, null);
        }
        catch (Exception ex)
        {
            return new ArchiveListing([], false, ex.Message);
        }
    }
}
