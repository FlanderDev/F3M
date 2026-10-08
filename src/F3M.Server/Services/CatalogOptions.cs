using F3M.Shared.Helpers;

namespace F3M.Server.Services;

/// <summary>Settings for the public catalog (plan section 5). Bound from the "Catalog" configuration section.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>Folder published read-only at /catalog. Holds index.json, keys.json and mods/&lt;groupId&gt;/&lt;versionId&gt;.json.</summary>
    public string Directory { get; set; } = Path.Combine(Assets.PublicContent, "catalog");

    /// <summary>PEM file with the ECDSA P-256 private key. Keep it outside the web root and out of source control.</summary>
    public string KeyPath { get; set; } = Path.Combine(Assets.StorageRoot, "Secrets", "catalog-signing.pem");

    /// <summary>Written into every signed envelope, so clients can pick the right public key during rotation.</summary>
    public string KeyId { get; set; } = "catalog-1";
}
