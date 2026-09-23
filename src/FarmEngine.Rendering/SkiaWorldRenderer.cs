using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// SkiaSharp port of <c>createCanvas2dRenderer</c> (packages/renderer-canvas2d/src/index.ts).
/// Draws a <see cref="WorldSnapshot"/> onto any <see cref="SKCanvas"/>: tiles
/// (background/overlay/object layers), crops, gathering nodes, machines, ladders, items,
/// NPCs/animals, the player with its facing dot, floating pops and the edit-mode grid.
/// Coordinates are world pixels at 1× — hosts apply their own zoom on the canvas matrix.
/// Stateless apart from the decoded-image cache; safe to call from any one thread at a time.
/// </summary>
public sealed class SkiaWorldRenderer : IDisposable
{
    private static readonly SKColor BackgroundTint = CssColor.Parse("#1a1a2e10");

    private readonly ImageStore _images;
    private readonly bool _ownsImages;
    private readonly Lock _gate = new();
    private SKTypeface? _popTypeface;
    private bool _ownsTypeface = true;

    public SkiaWorldRenderer(ImageStore? images = null)
    {
        _ownsImages = images is null;
        _images = images ?? new ImageStore();
    }

    public ImageStore Images => _images;

    /// <summary>
    /// Typeface for floating pops (canvas <c>bold system-ui</c>). Hosts pass their UI font;
    /// null uses the platform's default bold face.
    /// </summary>
    public SKTypeface? PopTypeface
    {
        get => _popTypeface;
        set
        {
            lock (_gate)
            {
                _popTypeface = value;
                _ownsTypeface = false;
            }
        }
    }

    /// <summary>
    /// Viewport size in world pixels: the camera size when present, else the whole world.
    /// This is the canvas size the TS host would allocate.
    /// </summary>
    public static (double Width, double Height) ViewportSize(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Camera is { } camera ? (camera.Width, camera.Height) : snapshot.WorldPixelSize();
    }

    /// <summary>Draws <paramref name="snapshot"/> at the canvas' current transform.</summary>
    public void Render(SKCanvas canvas, WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            RenderCore(canvas, snapshot);
        }
    }

    /// <summary>Renders into a new bitmap of the viewport size × <paramref name="scale"/> (tests, thumbnails).</summary>
    public SKBitmap RenderToBitmap(WorldSnapshot snapshot, float scale = 1)
    {
        var (width, height) = ViewportSize(snapshot);
        var bitmap = new SKBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        Render(canvas, snapshot);
        canvas.Flush();
        return bitmap;
    }

    public void Dispose()
    {
        if (_ownsTypeface)
        {
            _popTypeface?.Dispose();
        }

        if (_ownsImages)
        {
            _images.Dispose();
        }
    }

    private void RenderCore(SKCanvas canvas, WorldSnapshot snapshot)
    {
        var pixelArt = snapshot.PixelArt != false;
        // Pixel art: nearest-neighbour sampling (canvas imageSmoothingEnabled = false).
        var filterQuality = pixelArt ? SKFilterQuality.None : SKFilterQuality.Medium;
        var ts = snapshot.TileSize;
        var padding = snapshot.Padding;
        var pitch = snapshot.Pitch;
        var camera = snapshot.Camera;
        var (viewWidth, viewHeight) = ViewportSize(snapshot);

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        using var shape = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var imagePaint = new SKPaint { IsAntialias = false, FilterQuality = filterQuality };

        // clearRect + faint tint over the whole canvas.
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewWidth, (float)viewHeight));
        fill.Color = BackgroundTint;
        canvas.DrawRect(0, 0, (float)viewWidth, (float)viewHeight, fill);

        if (camera is not null)
        {
            canvas.Translate((float)-camera.X, (float)-camera.Y);
        }

        // Visible tile range (everything when there is no camera).
        var xStart = camera is not null ? Math.Max(0, (int)Math.Floor((camera.X - padding) / pitch)) : 0;
        var xEnd = camera is not null ? Math.Min(snapshot.Width - 1, (int)Math.Ceiling((camera.X + camera.Width - padding) / pitch)) : snapshot.Width - 1;
        var yStart = camera is not null ? Math.Max(0, (int)Math.Floor((camera.Y - padding) / pitch)) : 0;
        var yEnd = camera is not null ? Math.Min(snapshot.Height - 1, (int)Math.Ceiling((camera.Y + camera.Height - padding) / pitch)) : snapshot.Height - 1;

        bool DrawSprite(SnapshotSprite? sprite, double dx, double dy, double dw, double dh, double opacity = 1)
        {
            if (sprite is null)
            {
                return false;
            }

            var img = _images.Get(sprite.ImageUrl);
            if (img is null || img.Width == 0 || img.Height == 0)
            {
                return false;
            }

            var sw = sprite.FrameWidth != 0 ? sprite.FrameWidth : img.Width;
            var sh = sprite.FrameHeight != 0 ? sprite.FrameHeight : img.Height;
            var sx = sprite.SourceX ?? (sprite.Frame * sw);
            var sy = sprite.SourceY ?? (sprite.Row * sh);
            if (sx < 0 || sy < 0 || sx + sw > img.Width || sy + sh > img.Height)
            {
                return false;
            }

            var scale = Math.Min(dw / sw, dh / sh);
            var dest = SKRect.Create((float)(dx + ((dw - (sw * scale)) / 2)), (float)(dy + dh - (sh * scale)), (float)(sw * scale), (float)(sh * scale));
            imagePaint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * opacity));
            canvas.DrawImage(img, SKRect.Create((float)sx, (float)sy, (float)sw, (float)sh), dest, imagePaint);
            return true;
        }

        void DrawWholeImage(SKImage img, double x, double y, double w, double h)
        {
            imagePaint.Color = SKColors.White;
            canvas.DrawImage(img, SKRect.Create(0, 0, img.Width, img.Height), SKRect.Create((float)x, (float)y, (float)w, (float)h), imagePaint);
        }

        // Tiles
        for (var y = yStart; y <= yEnd; y++)
        {
            if (y >= snapshot.Tiles.Count)
            {
                break;
            }

            var row = snapshot.Tiles[y];
            for (var x = xStart; x <= xEnd && x < row.Count; x++)
            {
                var tile = row[x];
                var px = padding + (x * pitch);
                var py = padding + (y * pitch);
                var tileRect = SKRect.Create((float)px, (float)py, (float)ts, (float)ts);

                fill.Color = Canvas2d.TileColor(tile.Background, "grass");
                canvas.DrawRect(tileRect, fill);
                if (tile.ImageUrl is not null)
                {
                    if (_images.Get(tile.ImageUrl) is { Width: > 0 } img)
                    {
                        DrawWholeImage(img, px, py, ts, ts);
                    }
                }
                else
                {
                    DrawSprite(ArtLayer(tile, 0), px, py, ts, ts);
                    if (tile.Overlay is not null && !DrawSprite(ArtLayer(tile, 1), px, py, ts, ts))
                    {
                        fill.Color = CssColor.WithOpacity(Canvas2d.TileColor(tile.Overlay, "path"), 0.7);
                        canvas.DrawRect(tileRect, fill);
                    }

                    if (tile.Object is not null && !DrawSprite(ArtLayer(tile, 2), px, py, ts, ts))
                    {
                        fill.Color = Canvas2d.TileColor(tile.Object, "wall");
                        canvas.DrawRect(tileRect, fill);
                    }
                }

                if (tile.Node is { Sprite: null } node)
                {
                    // Gathering node: filled circle in the node color; faded while depleted.
                    var opacity = node.Depleted ? 0.35 : 1;
                    var radius = (float)Math.Floor(ts * 0.32);
                    var cx = (float)(px + (ts / 2));
                    var cy = (float)(py + (ts / 2));
                    shape.Color = CssColor.WithOpacity(CssColor.Parse(node.Color), opacity);
                    canvas.DrawCircle(cx, cy, radius, shape);
                    stroke.Color = CssColor.WithOpacity(CssColor.Parse("#00000040"), opacity);
                    stroke.StrokeWidth = 1;
                    canvas.DrawCircle(cx, cy, radius, stroke);
                }

                if (tile.Node?.Sprite is { } nodeSprite)
                {
                    DrawSprite(nodeSprite, px, py, ts, ts, tile.Node.Depleted ? 0.35 : 1);
                }

                if (tile.Machine is { Sprite: null } machine)
                {
                    var size = Math.Floor(ts * 0.7);
                    var mx = px + ((ts - size) / 2);
                    var my = py + ((ts - size) / 2);
                    var box = new SKRoundRect(SKRect.Create((float)mx, (float)my, (float)size, (float)size), 3);
                    shape.Color = CssColor.Parse(machine.Color);
                    canvas.DrawRoundRect(box, shape);
                    stroke.Color = CssColor.Parse("#00000060");
                    stroke.StrokeWidth = 1.5f;
                    canvas.DrawRoundRect(box, stroke);
                    if (machine.OutputReady)
                    {
                        shape.Color = CssColor.Parse("#ffd94a");
                        canvas.DrawCircle((float)(px + (ts * 0.72)), (float)(py + (ts * 0.3)), (float)Math.Floor(ts * 0.12), shape);
                    }
                    else if (machine.Working)
                    {
                        fill.Color = CssColor.Parse("#ffffff70");
                        canvas.DrawRect(SKRect.Create((float)(mx + (size * 0.35)), (float)(my + (size * 0.35)), (float)(size * 0.3), (float)(size * 0.3)), fill);
                    }
                }

                if (tile.Machine is not null)
                {
                    DrawSprite(tile.Machine.Sprite, px, py, ts, ts);
                }

                if (tile.LadderDown)
                {
                    fill.Color = CssColor.Parse("#241a10");
                    var hole = Math.Floor(ts * 0.6);
                    canvas.DrawRect(SKRect.Create((float)(px + ((ts - hole) / 2)), (float)(py + ((ts - hole) / 2)), (float)hole, (float)hole), fill);
                    stroke.Color = CssColor.Parse("#c9a95e");
                    stroke.StrokeWidth = 2;
                    var lx = px + (ts / 2);
                    var top = (float)(py + ((ts - hole) / 2) + 2);
                    var bottom = (float)(py + ((ts + hole) / 2) - 2);
                    canvas.DrawLine((float)(lx - (hole * 0.2)), top, (float)(lx - (hole * 0.2)), bottom, stroke);
                    canvas.DrawLine((float)(lx + (hole * 0.2)), top, (float)(lx + (hole * 0.2)), bottom, stroke);
                }

                if (tile.Crop is { Sprite: null } crop)
                {
                    var colorIndex = Math.Clamp(crop.ColorIndex, 0, Canvas2d.CropStageColors.Count - 1);
                    var color = CssColor.Parse(crop.Withered ? Canvas2d.WitheredCropColor : Canvas2d.CropStageColors[colorIndex]);
                    var cropSize = Math.Floor(ts * 0.35);
                    var cropRect = SKRect.Create((float)(px + ((ts - cropSize) / 2)), (float)(py + ((ts - cropSize) / 2)), (float)cropSize, (float)cropSize);
                    if (!crop.Withered && crop.Mature)
                    {
                        // Canvas shadowBlur 8 ≈ Gaussian sigma 4: a golden glow behind ripe crops.
                        using var glow = new SKPaint
                        {
                            Color = CssColor.Parse("#d6bd2499"),
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4),
                            IsAntialias = true,
                        };
                        canvas.DrawRect(cropRect, glow);
                    }

                    fill.Color = color;
                    canvas.DrawRect(cropRect, fill);
                }

                if (tile.Crop is not null)
                {
                    DrawSprite(tile.Crop.Sprite, px, py, ts, ts);
                }

                if (tile.Item is { } item)
                {
                    if (DrawSprite(item.Sprite, px, py, ts, ts))
                    {
                        continue;
                    }

                    if (item.ImageUrl is not null)
                    {
                        if (_images.Get(item.ImageUrl) is { Width: > 0 } img)
                        {
                            var itemSize = Math.Floor(ts * 0.5);
                            DrawWholeImage(img, px + ((ts - itemSize) / 2), py + ((ts - itemSize) / 2), itemSize, itemSize);
                        }
                    }
                    else
                    {
                        shape.Color = CssColor.Parse(Canvas2d.ItemColor);
                        canvas.DrawCircle((float)(px + (ts / 2)), (float)(py + (ts / 2)), (float)Math.Floor(ts * 0.15), shape);
                    }
                }
            }
        }

        // NPCs (and animals)
        foreach (var npc in snapshot.Npcs)
        {
            var px = Canvas2d.EntityPixelOrigin(npc.X, padding, pitch);
            var py = Canvas2d.EntityPixelOrigin(npc.Y, padding, pitch);
            var npcW = Math.Floor(ts * 0.65);
            var npcH = Math.Floor(ts * 0.75);

            if (npc.Sprite is not null && DrawSprite(npc.Sprite, px, py, ts, ts))
            {
                continue;
            }

            if (npc.ImageUrl is not null && _images.Get(npc.ImageUrl) is { Width: > 0 } npcImage)
            {
                DrawWholeImage(npcImage, px + ((ts - npcW) / 2), py + ((ts - npcH) / 2), npcW, npcH);
                continue;
            }

            var body = new SKRoundRect(SKRect.Create((float)(px + ((ts - npcW) / 2)), (float)(py + ((ts - npcH) / 2)), (float)npcW, (float)npcH), 2);
            shape.Color = CssColor.Parse(npc.Color ?? Canvas2d.NpcColor);
            canvas.DrawRoundRect(body, shape);
            stroke.Color = CssColor.Parse(Canvas2d.NpcBorderColor);
            stroke.StrokeWidth = 1;
            canvas.DrawRoundRect(body, stroke);
        }

        // Player
        var player = snapshot.Player;
        var drawX = player.PixelX ?? Canvas2d.EntityPixelOrigin(player.X, padding, pitch);
        var drawY = player.PixelY ?? Canvas2d.EntityPixelOrigin(player.Y, padding, pitch);
        var playerW = Math.Floor(ts * 0.65);
        var playerH = Math.Floor(ts * 0.75);

        var drewImage = player.Sprite is not null && DrawSprite(player.Sprite, drawX, drawY, ts, ts);
        if (!drewImage && player.ImageUrl is not null && _images.Get(player.ImageUrl) is { Width: > 0 } playerImage)
        {
            DrawWholeImage(playerImage, drawX + ((ts - playerW) / 2), drawY + ((ts - playerH) / 2), playerW, playerH);
            drewImage = true;
        }

        if (!drewImage)
        {
            var body = new SKRoundRect(SKRect.Create((float)(drawX + ((ts - playerW) / 2)), (float)(drawY + ((ts - playerH) / 2)), (float)playerW, (float)playerH), 2);
            shape.Color = CssColor.Parse(Canvas2d.PlayerColor);
            canvas.DrawRoundRect(body, shape);
            stroke.Color = CssColor.Parse(Canvas2d.PlayerBorderColor);
            stroke.StrokeWidth = 2;
            canvas.DrawRoundRect(body, stroke);

            var (dx, dy) = Canvas2d.DirectionOffset(player.Direction);
            var dotSize = Math.Floor(ts * 0.1);
            var centerX = drawX + (ts / 2);
            var centerY = drawY + (ts / 2);
            var dotX = centerX + (dx * ((playerW / 2) - (dotSize / 2))) - (dotSize / 2);
            var dotY = centerY + (dy * ((playerH / 2) - (dotSize / 2))) - (dotSize / 2);
            shape.Color = CssColor.Parse("#ffffffcc");
            canvas.DrawCircle((float)(dotX + (dotSize / 2)), (float)(dotY + (dotSize / 2)), (float)(dotSize / 2), shape);
        }

        // Juice: floating feedback text rises and fades with age (M7).
        if (snapshot.Pops is { Count: > 0 } pops)
        {
            _popTypeface ??= SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
            var textSize = (float)Math.Max(11, Math.Floor(ts * 0.42));
            using var textStroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, Typeface = _popTypeface, TextSize = textSize, TextAlign = SKTextAlign.Center };
            using var textFill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Typeface = _popTypeface, TextSize = textSize, TextAlign = SKTextAlign.Center };
            foreach (var pop in pops)
            {
                var age = Math.Clamp(pop.Age, 0, 1);
                var px = Canvas2d.EntityPixelOrigin(pop.X, padding, pitch) + (ts / 2);
                var py = Canvas2d.EntityPixelOrigin(pop.Y, padding, pitch) - (age * ts * 0.8);
                var opacity = 1 - (age * age);
                textStroke.Color = CssColor.WithOpacity(CssColor.Parse("#00000090"), opacity);
                textFill.Color = CssColor.WithOpacity(CssColor.Parse(pop.Color ?? "#ffd94a"), opacity);
                canvas.DrawText(pop.Text, (float)px, (float)py, textStroke);
                canvas.DrawText(pop.Text, (float)px, (float)py, textFill);
            }
        }

        // Edit-mode grid overlay
        if (snapshot.GridOverlay)
        {
            stroke.Color = CssColor.Parse("#ffffff10");
            stroke.StrokeWidth = 0.5f;
            for (var y = yStart; y <= yEnd; y++)
            {
                for (var x = xStart; x <= xEnd; x++)
                {
                    canvas.DrawRect(SKRect.Create((float)(padding + (x * pitch)), (float)(padding + (y * pitch)), (float)ts, (float)ts), stroke);
                }
            }
        }

        canvas.Restore();
    }

    private static SnapshotSprite? ArtLayer(SnapshotTile tile, int index) =>
        tile.ArtLayers is { } layers && index < layers.Length ? layers[index] : null;
}
