using F3M.Shared.Helpers;

namespace F3M.Server.Services;

/// <summary>Settings for the public catalog (plan section 5). Bound from the "Catalog" configuration section.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>Folder published read-only at /catalog. Holds index.json, keys.json and mods/&lt;groupId&gt;/&lt;versionId&gt;.json.</summary>
    public string Directory { get; set; } = Path.Combine(Assets.PublicContent, "catalog");

    /// <summary>
    /// Encrypted PEM file with the ECDSA P-256 private key. Created on first start if missing. Lives in the storage
    /// volume (see compose.yaml), outside the web root, and is never committed.
    /// </summary>
    public string KeyPath { get; set; } = Path.Combine(Assets.StorageRoot, "Secrets", "catalog-signing.pem");

    /// <summary>
    /// Password that encrypts the private key. Set it with the Catalog__KeyPassword environment variable.
    /// If unset, Development uses <see cref="DevelopmentKeyPassword"/>; any other environment refuses to start.
    /// </summary>
    public string? KeyPassword { get; set; }

    /// <summary>Password used only when running in Development and <see cref="KeyPassword"/> is not set. Never valid in production.</summary>
    public const string DevelopmentKeyPassword = "F3M";

    /// <summary>Written into every signed envelope, so clients can pick the right public key during rotation.</summary>
    public string KeyId { get; set; } = "catalog-1";
}
