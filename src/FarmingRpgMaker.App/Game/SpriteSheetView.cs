using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// The art studio's sprite sheet (web AssetManager "Click a cell to append it"): the selected
/// asset's whole image at a whole-number zoom, drawn pixelated under a grid of the current frame
/// size. A click reports the image pixel under the pointer; the art studio decides what it adds.
/// </summary>
internal sealed class SpriteSheetView : Control
{
    /// <summary>Sheets up to this many pixels on their longest side are zoomed in (at most 4×).</summary>
    private const int ZoomedSide = 256;
    // Each grid line is a dark and a light pixel side by side, so it reads over any art.
    private static readonly ImmutablePen DarkPen = new(new ImmutableSolidColorBrush(Color.FromArgb(130, 0, 0, 0)), 1);
    private static readonly ImmutablePen LightPen = new(new ImmutableSolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
    private static readonly ImmutableSolidColorBrush HoverFill = new(Color.FromArgb(80, 80, 160, 255));
    private static readonly ImmutableSolidColorBrush OutsideFill = new(Color.FromArgb(90, 128, 128, 128));
    private Bitmap? _image;
    private int _cellWidth;
    private int _cellHeight;
    private PixelPoint? _hover;

    public SpriteSheetView()
    {
        Name = "ArtSheet";
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Sprite sheet: click a cell to add it as a frame");
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    /// <summary>Raised with the image pixel (x, y) that was clicked.</summary>
    public event Action<double, double>? CellClicked;

    /// <summary>The image shown, or null.</summary>
    public Bitmap? Image => _image;

    /// <summary>Screen pixels per image pixel: 1–4, larger for smaller sheets.</summary>
    public int Zoom { get; private set; } = 1;

    /// <summary>Shows <paramref name="image"/> (null shows nothing) at a zoom that suits its size.</summary>
    public void SetImage(Bitmap? image)
    {
        _image = image;
        _hover = null;
        var side = image is null ? 0 : Math.Max(image.PixelSize.Width, image.PixelSize.Height);
        Zoom = side == 0 ? 1 : Math.Clamp(ZoomedSide / side, 1, 4);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>The grid's cell size in image pixels; zero or less hides the grid.</summary>
    public void SetCell(int width, int height)
    {
        if (width == _cellWidth && height == _cellHeight)
        {
            return;
        }

        _cellWidth = width;
        _cellHeight = height;
        _hover = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _image is null ? default : new Size(_image.PixelSize.Width * Zoom, _image.PixelSize.Height * Zoom);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_image is null)
        {
            return;
        }

        var width = _image.PixelSize.Width;
        var height = _image.PixelSize.Height;
        // A transparent fill keeps the whole sheet hit-testable for clicks.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawImage(_image, new Rect(0, 0, width, height), new Rect(0, 0, width * Zoom, height * Zoom));
        context.DrawRectangle(null, DarkPen, new Rect(0.5, 0.5, (width * Zoom) - 1, (height * Zoom) - 1));
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        // Cells that would reach past the image cannot become frames: dim what they cover.
        var fullWidth = width / _cellWidth * _cellWidth;
        var fullHeight = height / _cellHeight * _cellHeight;
        if (fullWidth < width)
        {
            context.FillRectangle(OutsideFill, new Rect(fullWidth * Zoom, 0, (width - fullWidth) * Zoom, height * Zoom));
        }

        if (fullHeight < height)
        {
            context.FillRectangle(OutsideFill, new Rect(0, fullHeight * Zoom, fullWidth * Zoom, (height - fullHeight) * Zoom));
        }

        if (_hover is { } cell && (cell.X + 1) * _cellWidth <= width && (cell.Y + 1) * _cellHeight <= height)
        {
            context.FillRectangle(HoverFill, new Rect(cell.X * _cellWidth * Zoom, cell.Y * _cellHeight * Zoom, _cellWidth * Zoom, _cellHeight * Zoom));
        }

        for (var x = _cellWidth; x < width; x += _cellWidth)
        {
            context.DrawLine(DarkPen, new Point((x * Zoom) - 0.5, 0), new Point((x * Zoom) - 0.5, height * Zoom));
            context.DrawLine(LightPen, new Point((x * Zoom) + 0.5, 0), new Point((x * Zoom) + 0.5, height * Zoom));
        }

        for (var y = _cellHeight; y < height; y += _cellHeight)
        {
            context.DrawLine(DarkPen, new Point(0, (y * Zoom) - 0.5), new Point(width * Zoom, (y * Zoom) - 0.5));
            context.DrawLine(LightPen, new Point(0, (y * Zoom) + 0.5), new Point(width * Zoom, (y * Zoom) + 0.5));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        if (cell != _hover)
        {
            _hover = cell;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_image is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        e.Handled = true;
        CellClicked?.Invoke(point.X / Zoom, point.Y / Zoom);
    }

    private PixelPoint? CellAt(Point point)
    {
        if (_image is null || _cellWidth <= 0 || _cellHeight <= 0 || point.X < 0 || point.Y < 0)
        {
            return null;
        }

        return new PixelPoint((int)Math.Floor(point.X / Zoom / _cellWidth), (int)Math.Floor(point.Y / Zoom / _cellHeight));
    }
}
