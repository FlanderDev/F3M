using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using F3M.Shared;

namespace F3M.Client.Components;

public partial class PickerImage
{
    /// <summary>Snapshot of a successfully chosen image, passed via <see cref="PickerImage.OnImageChanged"/>.</summary>
    public sealed record SelectedImageInfo(byte[] Bytes, string FileName, string DataUrl);

    // ── Parameters ────────────────────────────────────────────────────────────

    /// <summary>Hint shown in the label, e.g. "optional" or "leave blank to keep existing".</summary>
    [Parameter] public string HintText { get; set; } = "optional";

    // Two-way bindable outputs so the parent can read the chosen image without
    // subscribing to a callback if it prefers the simpler bind-* syntax.
    [Parameter] public byte[]?  ImageBytes    { get; set; }
    [Parameter] public string   ImageFileName { get; set; } = string.Empty;
    [Parameter] public string?  PreviewDataUrl { get; set; }

    [Parameter] public EventCallback<byte[]?>  ImageBytesChanged    { get; set; }
    [Parameter] public EventCallback<string>   ImageFileNameChanged { get; set; }
    [Parameter] public EventCallback<string?>  PreviewDataUrlChanged { get; set; }

    /// <summary>Fired after a valid image is selected or cleared. Null = cleared.</summary>
    [Parameter] public EventCallback<SelectedImageInfo?> OnImageChanged { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    public string? Error { get; private set; }

    private const long MaxImage = 8L * 1024 * 1024;

    // ── Handlers ──────────────────────────────────────────────────────────────

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        Error = null;
        var f   = e.File;
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();

        if (!Configuration.AllowedThumbnailFormat.Contains(ext))
        {
            Error = $"Image type '{ext}' not allowed.";
            return;
        }
        if (f.Size > MaxImage)
        {
            Error = "Image exceeds 8 MB.";
            return;
        }

        await using var stream = f.OpenReadStream(MaxImage);
        using  var ms     = new MemoryStream((int)f.Size);
        await stream.CopyToAsync(ms);

        var bytes = ms.ToArray();
        var mime  = ext is ".jpg" or ".jpeg" ? "image/jpeg"
                  : ext == ".png"            ? "image/png"
                                             : "image/webp";
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

        await ImageBytesChanged.InvokeAsync(bytes);
        await ImageFileNameChanged.InvokeAsync(f.Name);
        await PreviewDataUrlChanged.InvokeAsync(dataUrl);
        await OnImageChanged.InvokeAsync(new SelectedImageInfo(bytes, f.Name, dataUrl));
    }

    private async Task Clear()
    {
        Error = null;
        await ImageBytesChanged.InvokeAsync(null);
        await ImageFileNameChanged.InvokeAsync(string.Empty);
        await PreviewDataUrlChanged.InvokeAsync(null);
        await OnImageChanged.InvokeAsync(null);
    }
}
