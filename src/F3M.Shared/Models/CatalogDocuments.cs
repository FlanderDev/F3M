using System;
using System.Collections.Generic;

namespace F3M.Shared.Models;

/// <summary>Records of the signed catalog published under catalog/. Shared so the website and desktop read the same shapes.</summary>
// ── Document shapes (camelCase on the wire, see JsonSerializerDefaults.Web) ──

public sealed record VersionDocument(
    int Schema,
    int GroupId,
    int VersionId,
    string Version,
    string Name,
    string Author,
    string Description,
    string Category,
    DateTime PublishedAt,
    string? PreviewUrl,
    IReadOnlyList<VersionFile> Files,
    IReadOnlyList<DependencyRef> Dependencies,
    IReadOnlyList<GeneratedPathRef> GeneratedPaths);

public sealed record VersionFile(
    int FileId,
    string OriginalName,
    bool IsArchive,
    string TargetPath,
    string Sha256,
    long Size,
    IReadOnlyList<PlacementRef> Placements);

public sealed record PlacementRef(string From, string To, string Sha256, long Size, string Kind);

/// <summary>"latest" follows the group's current approved version. Pins are local to a desktop profile, not published.</summary>
public sealed record DependencyRef(int GroupId, string Version);

public sealed record GeneratedPathRef(string Pattern, string Kind);

public sealed record IndexDocument(int Schema, DateTime GeneratedAt, IReadOnlyList<IndexGroup> Groups);

public sealed record IndexGroup(
    int GroupId,
    string Name,
    string Author,
    string Category,
    int LatestVersionId,
    string LatestVersion,
    DateTime UpdatedAt,
    string? PreviewUrl);

public sealed record KeysDocument(List<PublicKeyEntry> Keys);

public sealed record PublicKeyEntry(string KeyId, string Algorithm, string PublicKeyPem);

public sealed record SignedEnvelope(string KeyId, string Document, string Signature);

// ── Admin maintenance ──

/// <summary>Outcome of a full catalog rebuild, shown on the admin page.</summary>
/// <param name="FilesBackfilled">Files uploaded before catalog support whose placements were filled in.</param>
/// <param name="VersionsPublished">Approved versions now in the catalog.</param>
/// <param name="VersionsRemoved">Documents removed because their version no longer exists or is not approved.</param>
/// <param name="Errors">Versions left out because their files could not be read.</param>
public sealed record CatalogRebuildResult(
    int FilesBackfilled,
    int VersionsPublished,
    int VersionsRemoved,
    IReadOnlyList<string> Errors);
