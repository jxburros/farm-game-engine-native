using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FarmEngine.Interop;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Frames of visual bindings drawn by the Rust renderer (<see cref="RustPreview.RenderVisual"/>):
/// the art studio's animation preview shows exactly what the game draws.
/// </summary>
internal sealed class VisualPreview : IDisposable
{
    private RustPreview? _preview;
    private GameProject? _project;

    /// <summary>An image of <paramref name="visual"/> at <paramref name="tick"/>, or null when it resolves to nothing.</summary>
    public Image? Render(GameProject project, VisualRef visual, double tick, double size) => Draw(null, project, visual, tick, size);

    /// <summary>
    /// Like <see cref="Render"/>, for a preview drawn again and again (the art studio's animation):
    /// a frame the size of <paramref name="previous"/>'s bitmap is written into that bitmap and the
    /// same image comes back; otherwise the previous bitmap is freed and a new image made.
    /// </summary>
    public Image? Draw(Image? previous, GameProject project, VisualRef visual, double tick, double size)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            if (_preview is null || _preview.IsPoisoned)
            {
                _preview?.Dispose();
                _preview = RustPreview.Create(project);
                _project = project;
            }
            else if (!ReferenceEquals(project, _project))
            {
                _preview.SetProject(project);
                _project = project;
            }

            var frame = _preview.RenderVisual(visual, tick, size);
            if (frame.Width == 0 || frame.Height == 0)
            {
                Release(previous);
                return null;
            }

            var pixels = new PixelSize(frame.Width, frame.Height);
            if (previous?.Source is WriteableBitmap reused && reused.PixelSize == pixels)
            {
                Copy(frame, reused);
                previous.InvalidateVisual();
                return previous;
            }

            Release(previous);
            var bitmap = new WriteableBitmap(pixels, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
            Copy(frame, bitmap);
            var image = new Image { Source = bitmap, Stretch = Stretch.None };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
            return image;
        }
        catch (FarmFfiException)
        {
            Release(previous);
            return null;
        }
    }

    private static void Copy(PreviewFrame frame, WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        for (var y = 0; y < frame.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * frame.Width * 4, buffer.Address + (y * buffer.RowBytes), frame.Width * 4);
        }
    }

    /// <summary>Frees the bitmap of an image this preview made (it is no longer shown).</summary>
    public static void Release(Image? image)
    {
        if (image?.Source is WriteableBitmap bitmap)
        {
            image.Source = null;
            bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _preview = null;
        _project = null;
    }
}
