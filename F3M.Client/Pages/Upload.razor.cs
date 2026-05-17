using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using F3M.Client.Business;
using F3M.Client.Models;

namespace F3M.Client.Pages;

public partial class Upload
{
    [Parameter] 
    public int? GroupId { get; set; }

    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    private ModUploadDto dto = new();

    private bool IsNewVersion => GroupId.HasValue;
    private Mod? existingMod;

    // Passed directly into ModImagePicker via @bind-*
    private byte[]? imageBytes;
    private string  imageFileName  = string.Empty;
    private string? previewDataUrl;

    private string ImagePickerHint => IsNewVersion
        ? "optional, leave blank to keep existing"
        : "optional";

    // Passed by reference into ModFileList; the component mutates it in place.
    private readonly List<FileEntry> fileEntries = [];

    private string? uploadError;
    private bool    uploading;
    private bool    uploadSuccess;
    private int     uploadedId;
    private int     progress;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        if (!GroupId.HasValue) return;

        try
        {
            var result = await Http.GetFromJsonAsync<ModVersionsResult>($"api/mods/group/{GroupId}/versions");
            existingMod = result?.Versions.FirstOrDefault();
            if (existingMod is not null)
            {
                dto.Name        = existingMod.Name;
                dto.Category    = existingMod.Category;
                dto.Description = existingMod.Description;
                dto.ModGroupId  = GroupId;
            }
        }
        catch { /* non-critical — page still renders without pre-fill */ }
    }

    // ── Submit ────────────────────────────────────────────────────────────────

    private async Task HandleSubmit()
    {
        bool isAuthed = await AuthState.IsAuthenticatedAsync();
        if (!isAuthed)
        {
            uploadError = "You must be signed in to upload mods.";
            return;
        }
        if (fileEntries.Count == 0) return;

        uploading   = true;
        uploadError = null;
        progress    = 10;
        StateHasChanged();

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(dto.Name),        nameof(ModUploadDto.Name));
            content.Add(new StringContent(dto.Version),     nameof(ModUploadDto.Version));
            content.Add(new StringContent(dto.Category),    nameof(ModUploadDto.Category));
            content.Add(new StringContent(dto.Description), nameof(ModUploadDto.Description));

            if (dto.ModGroupId.HasValue)
                content.Add(new StringContent(dto.ModGroupId.Value.ToString()), nameof(ModUploadDto.ModGroupId));

            if (imageBytes is not null)
            {
                var imgPart = new ByteArrayContent(imageBytes);
                imgPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(imgPart, "previewImage", imageFileName);
            }

            progress = 20; StateHasChanged();

            foreach (var entry in fileEntries)
            {
                var filePart = new ByteArrayContent(entry.Bytes);
                filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(filePart, "files",         entry.OriginalName);
                content.Add(new StringContent(entry.InstallPath ?? string.Empty), "installPaths");
                content.Add(new StringContent(entry.OriginalName),                "originalNames");
            }

            progress = 50; StateHasChanged();

            var response = await Http.PostAsync("api/mods/upload", content);
            progress = 90; StateHasChanged();

            if (response.IsSuccessStatusCode)
            {
                var uploaded = await response.Content.ReadFromJsonAsync<Mod>();
                uploadedId   = uploaded?.Id ?? 0;
                uploadSuccess = true;
                progress      = 100;
            }
            else
            {
                uploadError = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Session expired. Please sign in again."
                    : await response.Content.ReadAsStringAsync();
            }
        }
        catch (Exception ex) { uploadError = ex.Message; }
        finally               { uploading  = false; }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    private void Reset()
    {
        dto           = new();
        fileEntries.Clear();
        imageBytes    = null;
        imageFileName = string.Empty;
        previewDataUrl = null;
        uploadError   = null;
        uploadSuccess = false;
        uploading     = false;
        progress      = 0;
        uploadedId    = 0;
    }
}
