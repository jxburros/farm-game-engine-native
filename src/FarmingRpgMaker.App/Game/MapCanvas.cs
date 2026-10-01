using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using FarmEngine.Interop;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Where Edit Mode draws a scene: its size in tiles and the (zoomed) tile size and padding,
/// with the editor's 1px grid seams between tiles.
/// </summary>
public sealed record MapGeometry(string SceneId, int Width, int Height, double TileSize, double Padding)
{
    /// <summary>Distance between tile origins (tile size + the 1px seam).</summary>
    public double Pitch => TileSize + 1;

    /// <summary>The whole map in world pixels (padding on every side).</summary>
    public Size WorldSize => new((Padding * 2) + (Width * Pitch) - 1, (Padding * 2) + (Height * Pitch) - 1);
}

/// <summary>
/// Edit Mode's map: the Rust renderer (<see cref="RustPreview"/>, farm-render) draws the
/// decorated editor snapshot of the scene, and this control shows it. Only the part of the map
/// visible in the surrounding <see cref="ScrollViewer"/> is rasterized, again when the project,
/// scene, zoom or scroll position changes. The control is sized to the whole map so scrolling
/// and hit testing work in world pixels (zoom is baked into <see cref="MapGeometry.TileSize"/>).
/// It takes keyboard focus (with a gold focus ring) so the map can be edited from the keyboard.
/// </summary>
public sealed class MapCanvas : Control, IDisposable
{
    /// <summary>Largest side rasterized when the visible area is unknown (no scroll viewer yet).</summary>
    private const double MaxUnclippedSide = 4096;

    private RustPreview? _preview;
    private GameProject? _pending;
    private int _projectVersion;
    private MapGeometry? _geometry;
    private WriteableBitmap? _bitmap;
    private (int Version, MapGeometry Geometry, PixelRect Region, double Scale)? _bitmapKey;
    private Rect _bitmapRect;
    private ScrollViewer? _scroller;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        // Web: focus-visible:ring-2 in --iw-gold-400, shown when focus arrives from the keyboard.
        FocusAdorner = new FuncTemplate<Control>(() => new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.Parse("#E7BA4B")),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(-3),
        });
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    /// <summary>The scene being shown and how it is laid out; null shows nothing.</summary>
    public MapGeometry? Geometry
    {
        get => _geometry;
        set
        {
            if (value == _geometry)
            {
                return;
            }

            var resized = value?.WorldSize != _geometry?.WorldSize;
            _geometry = value;
            if (resized)
            {
                InvalidateMeasure();
            }

            InvalidateVisual();
        }
    }

    /// <summary>Why nothing can be drawn (the Rust library is missing, or it failed), else null.</summary>
    public string? Error { get; private set; }

    /// <summary>Frames rasterized so far (tests).</summary>
    public int RenderCount { get; private set; }

    /// <summary>The last rasterized region in world pixels (tests).</summary>
    public Rect RenderedRegion => _bitmapRect;

    /// <summary>The renderer's preview once the map was drawn (tests).</summary>
    internal RustPreview? Preview => _preview;

    /// <summary>
    /// Hands the current project to the renderer (after every edit). It reaches Rust when the map
    /// is next drawn: edits made while another tab shows send nothing, and the tiles a paint
    /// stroke changes between two frames go over once. The preview then sends only the scenes an
    /// edit changed (<see cref="RustPreview.SetProject"/>).
    /// </summary>
    public void SetProject(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _pending = project;
        _projectVersion++;
        InvalidateVisual();
    }

    /// <summary>Sends the project <see cref="SetProject"/> received last, if it has not gone yet.</summary>
    private void ApplyPendingProject()
    {
        if (_pending is not { } project)
        {
            return;
        }

        _pending = null;
        try
        {
            if (_preview is null || _preview.IsPoisoned)
            {
                _preview?.Dispose();
                _preview = RustPreview.Create(project);
            }
            else
            {
                _preview.SetProject(project);
            }

            Error = null;
        }
        catch (FarmFfiException ex)
        {
            Error = $"The map can't be drawn: {ex.Message}";
            _preview?.Dispose();
            _preview = null;
        }
    }

    /// <summary>The tile under a control point, or null outside the map.</summary>
    public (int X, int Y)? TileAt(Point point)
    {
        if (_geometry is not { } g)
        {
            return null;
        }

        var tileX = (int)Math.Floor((point.X - g.Padding) / g.Pitch);
        var tileY = (int)Math.Floor((point.Y - g.Padding) / g.Pitch);
        return tileX >= 0 && tileX < g.Width && tileY >= 0 && tileY < g.Height ? (tileX, tileY) : null;
    }

    /// <summary>Control-space rectangle of a tile (hover and selection overlays).</summary>
    public Rect TileRect(int x, int y) =>
        _geometry is { } g ? new Rect(g.Padding + (x * g.Pitch), g.Padding + (y * g.Pitch), g.TileSize, g.TileSize) : default;

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _preview?.Dispose();
        _preview = null;
        _pending = null;
    }

    /// <summary>A focusable control element, so screen readers read its AutomationProperties.Name.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new MapCanvasAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize) =>
        _geometry is { } g ? new Size(Math.Ceiling(g.WorldSize.Width), Math.Ceiling(g.WorldSize.Height)) : default;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A transparent fill keeps the whole map hit-testable for pointer input.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        ApplyPendingProject();
        if (_geometry is not { } g || _preview is null)
        {
            return;
        }

        var region = VisibleRegion(g);
        if (region.Width < 1 || region.Height < 1)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var key = (_projectVersion, g, region, scale);
        if (_bitmap is null || _bitmapKey != key)
        {
            try
            {
                var frame = _preview.Render(g.SceneId, g.TileSize, g.Padding, new PreviewCamera(region.X, region.Y, region.Width, region.Height), scale);
                _bitmap?.Dispose();
                _bitmap = ToBitmap(frame, scale);
                _bitmapKey = key;
                _bitmapRect = new Rect(region.X, region.Y, frame.Width / scale, frame.Height / scale);
                RenderCount++;
                Error = null;
            }
            catch (FarmFfiException ex)
            {
                Error = $"The map can't be drawn: {ex.Message}";
                return;
            }
        }

        context.DrawImage(_bitmap, new Rect(_bitmap.Size), _bitmapRect);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    /// <summary>The part of the map inside the scroll viewer, in world pixels (whole pixels).</summary>
    private PixelRect VisibleRegion(MapGeometry g)
    {
        var world = new Rect(g.WorldSize);
        var visible = _scroller is { } scroller && this.TranslatePoint(default, scroller) is { } origin && scroller.Viewport.Width > 0
            ? new Rect(-origin.X, -origin.Y, scroller.Viewport.Width, scroller.Viewport.Height).Intersect(world)
            : new Rect(0, 0, Math.Min(world.Width, MaxUnclippedSide), Math.Min(world.Height, MaxUnclippedSide));

        var left = (int)Math.Floor(visible.X);
        var top = (int)Math.Floor(visible.Y);
        var right = (int)Math.Ceiling(visible.Right);
        var bottom = (int)Math.Ceiling(visible.Bottom);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static WriteableBitmap ToBitmap(PreviewFrame frame, double scale)
    {
        var bitmap = new WriteableBitmap(new PixelSize(Math.Max(1, frame.Width), Math.Max(1, frame.Height)), new Vector(96 * scale, 96 * scale), PixelFormat.Rgba8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }

    private sealed class MapCanvasAutomationPeer(MapCanvas owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
