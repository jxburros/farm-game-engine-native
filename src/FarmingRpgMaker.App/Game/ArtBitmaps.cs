using Avalonia.Media.Imaging;
using FarmEngine.Schemas;
using SkiaSharp;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Avalonia bitmaps of project art for the art studio: <see cref="CustomAsset.DataUrl"/> holds
/// the image as a base64 <c>data:image/…</c> URL (see <see cref="ArtImport"/>). Art can arrive in
/// imported projects and content packs without passing <see cref="ArtImport"/>, so every decode
/// first reads the size from the image header and refuses images over the import limits: a small
/// file that claims a huge image (a decompression bomb) never gets its pixels allocated.
/// </summary>
internal static class ArtBitmaps
{
    /// <summary>The image bytes of a base64 data URL, or null when it is not one.</summary>
    public static byte[]? Bytes(string? dataUrl)
    {
        if (dataUrl is null || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !dataUrl.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The width and height an encoded image declares in its header (nothing is decoded), or
    /// null when Skia can't read it.
    /// </summary>
    public static (int Width, int Height)? HeaderSize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>The art import limits: 1 to 8192 pixels per side and at most 16 megapixels.</summary>
    public static bool WithinLimits(int width, int height) =>
        width > 0 && height > 0 && width <= ArtImport.MaxSide && height <= ArtImport.MaxSide && (long)width * height <= ArtImport.MaxPixels;

    /// <summary>The whole image, or null when it can't be decoded or is over the import limits.</summary>
    public static Bitmap? Decode(string? dataUrl) => Load(dataUrl, (stream, _, _) => new Bitmap(stream));

    /// <summary>
    /// The image shrunk so its longest side is at most <paramref name="side"/> pixels (smaller
    /// images are decoded whole), or null. The size comes from the image header, not from the
    /// asset's stored width and height, which imported data may get wrong.
    /// </summary>
    public static Bitmap? Thumbnail(string? dataUrl, int side) => Load(dataUrl, (stream, width, height) =>
        width > side && width >= height ? Bitmap.DecodeToWidth(stream, side)
        : height > side ? Bitmap.DecodeToHeight(stream, side)
        : new Bitmap(stream));

    /// <summary>The asset's image as a <see cref="Thumbnail(string?, int)"/>.</summary>
    public static Bitmap? Thumbnail(CustomAsset asset, int side)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Thumbnail(asset.DataUrl, side);
    }

    private static Bitmap? Load(string? dataUrl, Func<Stream, int, int, Bitmap> decode)
    {
        if (Bytes(dataUrl) is not { } bytes)
        {
            return null;
        }

        try
        {
            if (HeaderSize(bytes) is not { } size || !WithinLimits(size.Width, size.Height))
            {
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            return decode(stream, size.Width, size.Height);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException or OutOfMemoryException)
        {
            return null;
        }
    }
}
