using F3M.Shared.Helpers;
using F3M.Client.Models;
using F3M.Client.Services;

namespace F3M.Client.Business;

/// <summary>
/// Builds the tree of what the game folder will contain once the selected files are placed
/// according to their install paths and archive folders (rules in <see cref="InstallPaths"/>).
/// </summary>
public static class InstallPreviewBuilder
{
    public static PreviewResult Build(IReadOnlyList<FileEntry> entries)
    {
        var root = new PreviewNode { Name = "Game folder" };
        var warnings = new List<string>();
        var fileCount = 0;
        long totalBytes = 0;

        void Warn(string text)
        {
            if (!warnings.Contains(text))
                warnings.Add(text);
        }

        void Add(string relative, long size, string source)
        {
            var parts = relative.Split('/');
            var node = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new PreviewNode { Name = parts[i], IsFile = last };
                    node.Children[parts[i]] = child;
                }
                else if (last || child.IsFile)
                {
                    // the spot is taken: by another file, or a file would have to be a folder (or the reverse)
                    child.Conflict = true;
                    Warn($"“{relative}” is claimed more than once; installing would fail.");
                }

                if (last && child.Source is null)
                {
                    child.Source = source;
                    child.Size = size;
                    fileCount++;
                    totalBytes += size;
                }

                node = child;
            }
        }

        foreach (var entry in entries)
        {
            if (!InstallPaths.TryPlan(entry.OriginalName, entry.InstallPath, out var placement))
            {
                Warn($"“{entry.OriginalName}”: the install path isn't valid.");
                continue;
            }

            if (!placement.IsArchive)
            {
                Add(placement.Target, entry.Size, entry.OriginalName);
                continue;
            }

            var listing = entry.Listing;
            if (listing is null)
                continue;

            if (listing.Error is not null)
            {
                Warn($"“{entry.OriginalName}”: couldn't read the archive ({listing.Error}).");
                continue;
            }

            if (listing.Entries.Count == 0)
            {
                Warn($"“{entry.OriginalName}” contains no files.");
                continue;
            }

            // Only the archive folder's contents are installed, without the folder itself.
            if (!InstallPaths.TrySplit(entry.ArchiveRoot, out var rootParts, out _))
            {
                Warn($"“{entry.OriginalName}”: the archive folder isn't valid.");
                continue;
            }

            var unsafeEntries = 0;
            foreach (var item in listing.Entries)
            {
                if (!InstallPaths.TrySplit(item.RelativePath, out var parts, out _) || parts.Count == 0)
                {
                    unsafeEntries++;
                    continue;
                }

                if (rootParts.Count > 0 && !InstallPaths.TryStripRoot(rootParts, parts, out parts))
                    continue;

                if (InstallPaths.TryExpand(placement.Target, string.Join('/', parts), out var relative))
                    Add(relative, item.Size, entry.OriginalName);
                else
                    unsafeEntries++;
            }

            if (unsafeEntries > 0)
                Warn($"“{entry.OriginalName}”: {unsafeEntries} entr{(unsafeEntries == 1 ? "y has" : "ies have")} a path that isn't allowed (like “..”) and would make the install fail.");

            if (listing.Truncated)
                Warn($"“{entry.OriginalName}”: only the first {ArchiveInspector.MaxEntries:N0} files are shown.");
        }

        CountFiles(root);
        return new PreviewResult(root, fileCount, totalBytes, warnings);
    }

    private static int CountFiles(PreviewNode node)
    {
        if (node.IsFile)
            return 1;

        node.FileCount = node.Children.Values.Sum(CountFiles);
        return node.FileCount;
    }
}
