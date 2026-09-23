using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FarmEngine.Rendering;

namespace FarmingRpgMaker.App.Game;

/// <summary>Avalonia images for custom art in panels (web <c>ItemArt</c>): data URL → cropped frame.</summary>
internal static class SpriteImage
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public static Image? Create(SnapshotSprite sprite, bool pixelArt)
    {
        var bitmap = Load(sprite.ImageUrl);
        if (bitmap is null)
        {
            return null;
        }

        var width = sprite.FrameWidth > 0 ? sprite.FrameWidth : bitmap.PixelSize.Width;
        var height = sprite.FrameHeight > 0 ? sprite.FrameHeight : bitmap.PixelSize.Height;
        var x = sprite.SourceX ?? (sprite.Frame * width);
        var y = sprite.SourceY ?? (sprite.Row * height);
        if (x < 0 || y < 0 || x + width > bitmap.PixelSize.Width || y + height > bitmap.PixelSize.Height)
        {
            return null;
        }

        var image = new Image
        {
            Source = new CroppedBitmap(bitmap, new PixelRect((int)x, (int)y, (int)width, (int)height)),
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapInterpolationMode(image, pixelArt ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        return image;
    }

    private static Bitmap? Load(string dataUrl)
    {
        if (Cache.TryGetValue(dataUrl, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        if (ImageStore.DataUrlBytes(dataUrl) is { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
#pragma warning disable CA1031 // Broken creator art falls back to the glyph.
            catch (Exception)
#pragma warning restore CA1031
            {
                bitmap = null;
            }
        }

        if (Cache.Count > 200)
        {
            Cache.Clear();
        }

        Cache[dataUrl] = bitmap;
        return bitmap;
    }
}
