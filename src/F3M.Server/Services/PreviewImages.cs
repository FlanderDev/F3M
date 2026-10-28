using ImageMagick;
using ImageMagick.Configuration;

namespace F3M.Server.Services;

/// <summary>The image formats a preview can be, as ImageMagick identifies them from the bytes (never from the name).</summary>
public enum PreviewFormat
{
    Png,
    Jpeg,
    Gif,
    WebP,
    Avif
}

/// <summary>
/// Checks preview images and stores them in a format every reader of the site understands. Browsers, Avalonia's Skia
/// (the desktop app) and the server's "nosniff" static files all need the file's real format, and its extension has to
/// say it: F95zone serves its attachments as AVIF whatever their name, so imported previews called "preview.jpg" are
/// AVIF inside. PNG, JPEG, GIF and WebP are kept as they are; AVIF, which the desktop app can't read, becomes WebP.
///
/// ImageMagick reads hundreds of formats, some of which run scripts, fetch URLs or read other files. The security
/// policy set in <see cref="Initialize"/> switches every format off except these five, for the whole process, and caps
/// the size, memory and time one image may take: whatever an upload claims to be, only these decoders ever see it.
/// </summary>
public static class PreviewImages
{
    /// <summary>Larger images are refused (by the policy) before their pixels are decoded.</summary>
    public const int MaxDimension = 8192;

    private const int WebPQuality = 90;

    private const string Unsupported = "The preview image isn't a PNG, JPEG, GIF, WebP or AVIF image of at most 8192 x 8192 pixels.";

    // Patterns are ImageMagick's format names; AVIF needs no other (its HEIC reader is not needed for it).
    private const string SecurityPolicy = """
        <policymap>
          <!-- Every format off, then only the preview formats back on (WEBP is also what AVIF becomes). -->
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read|write" pattern="{PNG,JPEG,GIF,WEBP,AVIF}" />
          <!-- Never run outside programs, never read files named inside an image. -->
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="path" rights="none" pattern="|*" />
          <!-- Upper limits for one image; code can't raise them. -->
          <policy domain="resource" name="width" value="8KP" />
          <policy domain="resource" name="height" value="8KP" />
          <policy domain="resource" name="memory" value="256MiB" />
          <policy domain="resource" name="disk" value="1GiB" />
          <policy domain="resource" name="time" value="30" />
        </policymap>
        """;

    /// <summary>
    /// Sets ImageMagick's security policy; call once at startup, before any image is read. Throws when a blocked format
    /// can still be read, so the server never runs without it. Returns a line for the log.
    /// </summary>
    public static string Initialize()
    {
        try
        {
            var files = ConfigurationFiles.Default;
            files.Policy.Data = SecurityPolicy;
            // A fixed folder for ImageMagick's configuration files, so restarts don't leave new ones behind.
            MagickNET.Initialize(files, Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "f3m-imagemagick")).FullName);

            // The check that the policy works: SVG must be refused.
            _ = new MagickImageInfo("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>"u8.ToArray());
        }
        catch (MagickPolicyErrorException)
        {
            // The version reads "ImageMagick 7.1.2-33 Q8 x64 ..."; the number is enough.
            return $"ImageMagick {MagickNET.ImageMagickVersion.Split(' ', 3)[1]} reads only PNG, JPEG, GIF, WebP and AVIF, at most {MaxDimension} x {MaxDimension} pixels.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            // No native library for this platform: nothing can be decoded at all, so there is nothing to protect.
            return "ImageMagick isn't available on this platform: previews are only checked, AVIF can't be converted.";
        }

        throw new InvalidOperationException("ImageMagick's security policy isn't in effect: it still reads SVG.");
    }

    /// <summary>
    /// The preview's format, as ImageMagick reads it from the header (the pixels aren't decoded). Throws
    /// <see cref="InvalidDataException"/> for any other format, or one too large.
    /// </summary>
    public static PreviewFormat Identify(byte[] bytes)
    {
        MagickImageInfo info;
        try
        {
            info = new MagickImageInfo(bytes);
        }
        catch (MagickException ex)
        {
            // A blocked format (policy), an unknown one, a broken file, or too large.
            throw new InvalidDataException(Unsupported, ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new InvalidDataException("This server can't read images. Upload the preview as PNG, JPEG or WebP.");
        }

        if (info.Width > MaxDimension || info.Height > MaxDimension)
            throw new InvalidDataException(Unsupported);

        return info.Format switch
        {
            MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 => PreviewFormat.Png,
            MagickFormat.Jpeg or MagickFormat.Jpg => PreviewFormat.Jpeg,
            MagickFormat.Gif or MagickFormat.Gif87 => PreviewFormat.Gif,
            MagickFormat.WebP => PreviewFormat.WebP,
            MagickFormat.Avif => PreviewFormat.Avif,
            _ => throw new InvalidDataException(Unsupported)
        };
    }

    /// <summary>The extension a stored preview of this format has; null for one that is converted (AVIF).</summary>
    public static string? Extension(PreviewFormat format) => format switch
    {
        PreviewFormat.Png => ".png",
        PreviewFormat.Jpeg => ".jpg",
        PreviewFormat.Gif => ".gif",
        PreviewFormat.WebP => ".webp",
        _ => null
    };

    /// <summary>
    /// The preview as it is stored, with its file extension: PNG, JPEG, GIF and WebP unchanged, AVIF as WebP. Throws
    /// <see cref="InvalidDataException"/> for anything else, or an AVIF that can't be read.
    /// </summary>
    public static (byte[] Bytes, string Extension) Normalize(byte[] bytes)
    {
        var format = Identify(bytes);
        if (Extension(format) is { } extension)
            return (bytes, extension);

        try
        {
            using var image = new MagickImage(bytes, new MagickReadSettings { Format = MagickFormat.Avif });
            image.AutoOrient();
            image.Format = MagickFormat.WebP;
            image.Quality = WebPQuality;
            return (image.ToByteArray(), ".webp");
        }
        catch (MagickException ex)
        {
            throw new InvalidDataException($"The AVIF preview image could not be read: {ex.Message}");
        }
    }
}
