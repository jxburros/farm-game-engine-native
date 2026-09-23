using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using FarmEngine.Rendering;
using SkiaSharp;

namespace FarmingRpgMaker.App.Game;

/// <summary>How <see cref="GameCanvas"/> maps world pixels to control pixels.</summary>
public enum CanvasZoomMode
{
    /// <summary>Scale the viewport to fit the control (play mode), centered.</summary>
    Fit,

    /// <summary>Use <see cref="GameCanvas.Zoom"/> as is; the control sizes itself to the scaled world (edit mode, in a scroll viewer).</summary>
    Fixed,
}

/// <summary>
/// Avalonia host for <see cref="SkiaWorldRenderer"/> — the native counterpart of the web
/// <c>&lt;canvas&gt;</c>. Draws the current <see cref="Snapshot"/> through a custom draw
/// operation that leases the SkiaSharp canvas, so tiles stay crisp at any zoom (vector
/// rects, nearest-neighbour art for pixel-art games).
/// </summary>
public sealed class GameCanvas : Control
{
    private WorldSnapshot? _snapshot;

    private static readonly Lazy<SKTypeface?> InterBold = new(LoadInterBold);

    public GameCanvas(SkiaWorldRenderer? renderer = null)
    {
        Renderer = renderer ?? new SkiaWorldRenderer();
        Renderer.PopTypeface ??= InterBold.Value;
        ClipToBounds = true;
    }

    /// <summary>The app's UI font (Inter Bold) for in-world text, like the web's system-ui.</summary>
    private static SKTypeface? LoadInterBold()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Bold.ttf"));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return SKTypeface.FromStream(copy);
        }
#pragma warning disable CA1031 // Missing font → platform default.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public SkiaWorldRenderer Renderer { get; }

    public CanvasZoomMode ZoomMode { get; set; } = CanvasZoomMode.Fit;

    /// <summary>World-pixel → control-pixel scale for <see cref="CanvasZoomMode.Fixed"/>; the effective scale otherwise.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>Scale actually used for the last layout (Fit computes it from the control size).</summary>
    public double EffectiveZoom { get; private set; } = 1;

    /// <summary>Top-left of the drawn viewport inside the control (centering in Fit mode).</summary>
    public Point ContentOffset { get; private set; }

    /// <summary>Background behind the world (letterbox area in Fit mode).</summary>
    public IBrush? Background { get; set; }

    public WorldSnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            var sizeChanged = _snapshot is null || value is null || SkiaWorldRenderer.ViewportSize(_snapshot) != SkiaWorldRenderer.ViewportSize(value);
            _snapshot = value;
            if (sizeChanged && ZoomMode == CanvasZoomMode.Fixed)
            {
                InvalidateMeasure();
            }

            InvalidateVisual();
        }
    }

    /// <summary>
    /// Picks the zoom for Fit mode: the largest scale that shows the preferred viewport inside
    /// the control, snapped to whole or half steps so pixel art stays even.
    /// </summary>
    public static double FitZoom(double hostWidth, double hostHeight, double preferredWidth, double preferredHeight)
    {
        if (hostWidth <= 0 || hostHeight <= 0 || preferredWidth <= 0 || preferredHeight <= 0)
        {
            return 1;
        }

        var raw = Math.Min(hostWidth / preferredWidth, hostHeight / preferredHeight);
        if (raw >= 1)
        {
            return Math.Max(1, Math.Floor(raw * 2) / 2);
        }

        return Math.Max(0.25, raw);
    }

    /// <summary>World-pixel point under a control point (null outside the drawn world).</summary>
    public Point? ControlToWorld(Point point)
    {
        if (_snapshot is null || EffectiveZoom <= 0)
        {
            return null;
        }

        var camera = _snapshot.Camera;
        var x = ((point.X - ContentOffset.X) / EffectiveZoom) + (camera?.X ?? 0);
        var y = ((point.Y - ContentOffset.Y) / EffectiveZoom) + (camera?.Y ?? 0);
        return new Point(x, y);
    }

    /// <summary>The tile under a control point, or null (web GameView <c>tileFromPointer</c>).</summary>
    public (int X, int Y)? TileAt(Point point)
    {
        if (_snapshot is null || ControlToWorld(point) is not { } world)
        {
            return null;
        }

        var pitch = _snapshot.Pitch;
        var tileX = (int)Math.Floor((world.X - _snapshot.Padding) / pitch);
        var tileY = (int)Math.Floor((world.Y - _snapshot.Padding) / pitch);
        return tileX >= 0 && tileX < _snapshot.Width && tileY >= 0 && tileY < _snapshot.Height ? (tileX, tileY) : null;
    }

    /// <summary>Control-space rectangle of a tile (hover highlight).</summary>
    public Rect TileRect(int x, int y)
    {
        if (_snapshot is null)
        {
            return default;
        }

        var camera = _snapshot.Camera;
        var left = ((_snapshot.Padding + (x * _snapshot.Pitch) - (camera?.X ?? 0)) * EffectiveZoom) + ContentOffset.X;
        var top = ((_snapshot.Padding + (y * _snapshot.Pitch) - (camera?.Y ?? 0)) * EffectiveZoom) + ContentOffset.Y;
        return new Rect(left, top, _snapshot.TileSize * EffectiveZoom, _snapshot.TileSize * EffectiveZoom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ZoomMode == CanvasZoomMode.Fixed && _snapshot is not null)
        {
            var (width, height) = SkiaWorldRenderer.ViewportSize(_snapshot);
            EffectiveZoom = Zoom;
            ContentOffset = default;
            return new Size(Math.Ceiling(width * Zoom), Math.Ceiling(height * Zoom));
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 416 : availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bounds = new Rect(Bounds.Size);
        if (Background is not null)
        {
            context.FillRectangle(Background, bounds);
        }

        if (_snapshot is null)
        {
            return;
        }

        var (viewWidth, viewHeight) = SkiaWorldRenderer.ViewportSize(_snapshot);
        double zoom;
        if (ZoomMode == CanvasZoomMode.Fit)
        {
            zoom = Math.Min(bounds.Width / viewWidth, bounds.Height / viewHeight);
            if (zoom >= 1)
            {
                zoom = Math.Max(1, Math.Floor(zoom * 4) / 4);
            }
        }
        else
        {
            zoom = Zoom;
        }

        EffectiveZoom = zoom <= 0 ? 1 : zoom;
        var drawWidth = viewWidth * EffectiveZoom;
        var drawHeight = viewHeight * EffectiveZoom;
        ContentOffset = ZoomMode == CanvasZoomMode.Fit
            ? new Point(Math.Round((bounds.Width - drawWidth) / 2), Math.Round((bounds.Height - drawHeight) / 2))
            : default;

        context.Custom(new WorldDrawOperation(bounds, _snapshot, Renderer, EffectiveZoom, ContentOffset));
    }

    private sealed class WorldDrawOperation(Rect bounds, WorldSnapshot snapshot, SkiaWorldRenderer renderer, double zoom, Point offset) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null)
            {
                return;
            }

            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            canvas.Save();
            canvas.ClipRect(SKRect.Create((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height));
            canvas.Translate((float)offset.X, (float)offset.Y);
            canvas.Scale((float)zoom);
            renderer.Render(canvas, snapshot);
            canvas.Restore();
        }

        public void Dispose()
        {
        }
    }
}
