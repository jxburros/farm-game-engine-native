using Avalonia.Media.Imaging;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Avalonia bitmaps of project art for the art studio: <see cref="CustomAsset.DataUrl"/> holds
/// the image as a base64 <c>data:image/…</c> URL (see <see cref="ArtImport"/>).
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

    /// <summary>The whole image, or null when it can't be decoded.</summary>
    public static Bitmap? Decode(string? dataUrl) => Load(dataUrl, stream => new Bitmap(stream));

    /// <summary>
    /// The asset's image shrunk so its longest side is at most <paramref name="side"/> pixels
    /// (smaller images and legacy assets without a size are decoded whole), or null.
    /// </summary>
    public static Bitmap? Thumbnail(CustomAsset asset, int side)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var width = asset.Width.Or(0);
        var height = asset.Height.Or(0);
        return Load(asset.DataUrl, stream =>
            width > side && width >= height ? Bitmap.DecodeToWidth(stream, side)
            : height > side ? Bitmap.DecodeToHeight(stream, side)
            : new Bitmap(stream));
    }

    private static Bitmap? Load(string? dataUrl, Func<Stream, Bitmap> decode)
    {
        if (Bytes(dataUrl) is not { } bytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return decode(stream);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
