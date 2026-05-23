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
    public string InstallPath { get; set; } = string.Empty;
}
