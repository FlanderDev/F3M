using Avalonia.Media.Imaging;
using ImageMagick;

namespace F3M.Desktop.Core;

/// <summary>
/// Turns image bytes from the server into a bitmap. Avalonia reads PNG, JPEG and WebP itself; AVIF it can't, and the
/// forum the community mods were imported from serves its attachments as AVIF whatever their name says
/// (a "preview.jpg" is AVIF inside). Those go through Magick.NET first.
/// </summary>
public static class Images
{
    public static Bitmap Decode(byte[] bytes)
    {
        if (!IsAvif(bytes))
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }

        // The format is given, so ImageMagick decodes AVIF only and never guesses another coder from the bytes.
        using var image = new MagickImage(bytes, MagickFormat.Avif);
        image.Format = MagickFormat.Png;
        using var png = new MemoryStream(image.ToByteArray());
        return new Bitmap(png);
    }

    /// <summary>An ISO media file whose brand is AVIF: "ftypavif" (a still image) or "ftypavis" (an image sequence).</summary>
    private static bool IsAvif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && (bytes[4..12].SequenceEqual("ftypavif"u8) || bytes[4..12].SequenceEqual("ftypavis"u8));
}
