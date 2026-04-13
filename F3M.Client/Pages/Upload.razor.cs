using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using System.Net.Http.Json;

namespace F3M.Client.Pages;

public partial class Upload
{
    [Parameter]
    public int? GroupId { get; set; }

    private ModUploadDto dto = new();

    // Is this a "new version" upload for an existing group?
    private bool IsNewVersion => GroupId.HasValue;
    private Mod? existingMod;   // latest version of the group (for display)

    // ── File entries list ─────────────────────────────────────────────────────
    // Must be a class (not a record) so InstallPath mutations via @oninput
    // are reflected on the same heap object (records are nominally immutable).
    private class FileEntry
    {
        public required string OriginalName { get; init; }
        public required long Size { get; init; }
        public required byte[] Bytes { get; init; }
        public string InstallPath { get; set; } = string.Empty;
    }

    private readonly List<FileEntry> fileEntries = [];

    // Incremented each time a file is added so the InputFile "Add file" button
    // gets a fresh @key, forcing Blazor to mount a brand-new <input type=file>
    // element with a fresh _blazorFilesById registration for the next pick.
    private int addFileKey = 0;

    // ── Preview image ─────────────────────────────────────────────────────────
    private byte[]? imageBytes;
    private string imageFileName = string.Empty;
    private string? previewDataUrl;

    private string? fileError;
    private string? imageError;
    private string? uploadError;
    private bool uploading;
    private bool uploadSuccess;
    private int uploadedId;
    private int progress;


    private const long MaxMod = 512L * 1024 * 1024;
    private const long MaxImage = 8L * 1024 * 1024;

    protected override async Task OnInitializedAsync()
    {
        if (!GroupId.HasValue) return;

        // Load the latest version so we can pre-fill name/category
        try
        {
            var result = await Http.GetFromJsonAsync<ModVersionsResult>($"api/mods/group/{GroupId}/versions");
            existingMod = result?.Versions.FirstOrDefault();
            if (existingMod is not null)
            {
                dto.Name = existingMod.Name;
                dto.Category = existingMod.Category;
                dto.Description = existingMod.Description;
                dto.ModGroupId = GroupId;
            }
        }
        catch { }
    }

    // ── Image handler ──────────────────────────────────────────────────────────
    private async Task OnImageSelected(InputFileChangeEventArgs e)
    {
        imageError = null;
        var f = e.File;
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        if (!Configuration.AllowedThumbnailFormat.Contains(ext)) { imageError = $"Image type '{ext}' not allowed."; return; }
        if (f.Size > MaxImage) { imageError = "Image exceeds 8 MB."; return; }

        await using var stream = f.OpenReadStream(MaxImage);
        using var ms = new MemoryStream((int)f.Size);
        await stream.CopyToAsync(ms);
        imageBytes = ms.ToArray();
        imageFileName = f.Name;

        var mime = ext is ".jpg" or ".jpeg" ? "image/jpeg"
                 : ext == ".png" ? "image/png"
                                            : "image/webp";
        previewDataUrl = $"data:{mime};base64,{Convert.ToBase64String(imageBytes)}";
    }

    private void ClearImage() { imageBytes = null; imageFileName = string.Empty; previewDataUrl = null; imageError = null; }

    // ── Mod file handler — eagerly buffer bytes, then rotate the InputFile key ──
    // The key increment causes Blazor to unmount the old <input type=file> and
    // mount a fresh one, giving it a new _blazorFilesById registration so the
    // next file pick works without "_blazorFilesById, e is null" crashes.
    private async Task OnModFileSelected(InputFileChangeEventArgs e)
    {
        fileError = null;
        var f = e.File;
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        if (!Configuration.AllowedFileFormat.Contains(ext)) { fileError = $"File type '{ext}' not allowed."; return; }
        if (f.Size > MaxMod) { fileError = "File exceeds 512 MB."; return; }

        // Read all bytes NOW, while _blazorFilesById still knows about this file
        await using var stream = f.OpenReadStream(MaxMod);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        fileEntries.Add(new FileEntry
        {
            OriginalName = f.Name,
            Size = f.Size,
            Bytes = ms.ToArray()
        });

        // Rotate the key → fresh <input type=file> for the next pick
        addFileKey++;
    }

    private void RemoveFile(int index)
    {
        if (index >= 0 && index < fileEntries.Count)
            fileEntries.RemoveAt(index);
    }

    // ── Submit ────────────────────────────────────────────────────────────────
    private async Task HandleSubmit()
    {
        if (fileEntries.Count == 0) return;

        uploading = true; uploadError = null; progress = 10;
        StateHasChanged();

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(dto.Name), "Name");
            content.Add(new StringContent(dto.Version), "Version");
            content.Add(new StringContent(dto.Category), "Category");
            content.Add(new StringContent(dto.Description ?? ""), "Description");
            if (dto.ModGroupId.HasValue)
                content.Add(new StringContent(dto.ModGroupId.Value.ToString()), "ModGroupId");

            // Preview image
            if (imageBytes is not null)
            {
                var imgPart = new ByteArrayContent(imageBytes);
                imgPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(imgPart, "previewImage", imageFileName);
            }

            progress = 20; StateHasChanged();

            // All mod files + their metadata (same index)
            for (int i = 0; i < fileEntries.Count; i++)
            {
                var entry = fileEntries[i];
                var filePart = new ByteArrayContent(entry.Bytes);
                filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(filePart, "files", entry.OriginalName);
                content.Add(new StringContent(entry.InstallPath ?? ""), "installPaths");
                content.Add(new StringContent(entry.OriginalName), "originalNames");
            }

            progress = 50; StateHasChanged();

            var response = await Http.PostAsync("api/mods/upload", content);
            progress = 90; StateHasChanged();

            if (response.IsSuccessStatusCode)
            {
                var uploaded = await response.Content.ReadFromJsonAsync<Mod>();
                uploadedId = uploaded?.Id ?? 0;
                uploadSuccess = true;
                progress = 100;
            }
            else
            {
                uploadError = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    uploadError = "Session expired. Please sign in again.";
            }
        }
        catch (Exception ex) { uploadError = ex.Message; }
        finally { uploading = false; }
    }

    private void Reset()
    {
        dto = new();
        fileEntries.Clear();
        imageBytes = null; imageFileName = string.Empty; previewDataUrl = null;
        fileError = null; imageError = null; uploadError = null;
        uploadSuccess = false; uploading = false; progress = 0; uploadedId = 0;
    }

    private static string FormatSize(long b) =>
        b >= 1_048_576 ? $"{b / 1_048_576.0:F1} MB" : $"{b / 1024.0:F1} KB";
}