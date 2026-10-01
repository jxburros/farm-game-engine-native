using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Shows the Rust player's frames: a premultiplied RGBA bitmap drawn over the control's bounds,
/// one frame pixel per device pixel when the frame matches <see cref="FrameSize"/>.
/// </summary>
public sealed class PlayerSurface : Control
{
    /// <summary>Most pixels a frame may have; larger surfaces render smaller and are scaled up.</summary>
    public const double PixelBudget = 1920 * 1200;

    private WriteableBitmap? _bitmap;

    public PlayerSurface()
    {
        Name = "PlayerSurface";
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>Frames presented so far (tests).</summary>
    public int FrameCount { get; private set; }

    /// <summary>Size of the last presented frame in pixels.</summary>
    public PixelSize PresentedSize => _bitmap?.PixelSize ?? default;

    /// <summary>
    /// The frame size to render for the current bounds: device pixels, scaled down to stay within
    /// <see cref="PixelBudget"/> (960 × 600 before the first layout).
    /// </summary>
    public PixelSize FrameSize()
    {
        var bounds = Bounds.Size;
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return new PixelSize(960, 600);
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        var shrink = Math.Min(1, Math.Sqrt(PixelBudget / (width * height)));
        return new PixelSize(Math.Max(1, (int)Math.Round(width * shrink)), Math.Max(1, (int)Math.Round(height * shrink)));
    }

    /// <summary>Converts a point in this control to frame pixels of the last presented frame.</summary>
    public Point ToFramePixels(Point point)
    {
        var size = _bitmap?.PixelSize ?? FrameSize();
        var bounds = Bounds.Size;
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return point;
        }

        return new Point(point.X * size.Width / bounds.Width, point.Y * size.Height / bounds.Height);
    }

    /// <summary>
    /// Copies the player's last frame (<paramref name="step"/>'s size) straight from Rust into the
    /// bitmap, the frame's only copy on its way to the screen, and schedules a redraw (UI
    /// thread). A stepped frame (no pixels) leaves the bitmap as it was.
    /// </summary>
    public void Present(PlayerStep step, RustPlayer player)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(player);
        if (step.Width <= 0 || step.Height <= 0)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != step.Width || _bitmap.PixelSize.Height != step.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(step.Width, step.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
        }

        using (var buffer = _bitmap.Lock())
        {
            player.CopyPixels(buffer.Address, buffer.RowBytes * step.Height, buffer.RowBytes);
        }

        FrameCount++;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        // Hit testing needs a fill even before the first frame.
        context.FillRectangle(Brushes.Black, bounds);
        if (_bitmap is not null)
        {
            var source = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            {
                context.DrawImage(_bitmap, source, bounds);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>Avalonia keys → the engine's key names (<c>KeyboardEvent.key</c> of a US layout, lower case).</summary>
public static class PlayerKeys
{
    public static string? FromAvalonia(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.Up => "arrowup",
        Key.Down => "arrowdown",
        Key.Left => "arrowleft",
        Key.Right => "arrowright",
        Key.Space => " ",
        Key.Enter => "enter",
        Key.Escape => "escape",
        Key.Tab => "tab",
        Key.Back => "backspace",
        Key.Delete => "delete",
        Key.Insert => "insert",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "pageup",
        Key.PageDown => "pagedown",
        Key.LeftShift or Key.RightShift => "shift",
        Key.LeftCtrl or Key.RightCtrl => "control",
        Key.LeftAlt or Key.RightAlt => "alt",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemPlus => "=",
        Key.Add => "+",
        Key.OemComma => ",",
        Key.OemPeriod or Key.Decimal => ".",
        Key.OemQuestion or Key.Divide => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => null,
    };
}
