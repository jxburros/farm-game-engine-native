using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// SkiaSharp port of <c>createCanvas2dRenderer</c> (packages/renderer-canvas2d/src/index.ts),
/// extended with the built-in pixel-art pack. Draws a <see cref="WorldSnapshot"/> onto any
/// <see cref="SKCanvas"/> in passes:
/// <list type="number">
/// <item>ground: tile background/overlay/object layers, soil states, ladders;</item>
/// <item>soft drop shadows under entities and objects;</item>
/// <item>y-sorted objects and entities (nodes, machines, crops, items, NPCs, animals, the
/// player) so tall things overlap whoever stands behind them;</item>
/// <item>atmosphere: day/night tint, seasonal foliage, rain or snow;</item>
/// <item>floating pops and the edit-mode grid.</item>
/// </list>
/// Art resolution per layer/entity: creator-bound <see cref="SnapshotSprite"/> or image →
/// built-in art (<see cref="Art"/>) → the original colored shapes. Coordinates are world
/// pixels at 1× — hosts apply their own zoom on the canvas matrix. Stateless apart from the
/// decoded-image cache; safe to call from any one thread at a time.
/// </summary>
public sealed class SkiaWorldRenderer : IDisposable
{
    private static readonly SKColor BackgroundTint = CssColor.Parse("#1a1a2e10");
    private static readonly SKColor ShadowColor = new(0, 0, 0, 0x48);
    private static readonly SKColor RainTint = new(0xC8, 0xD2, 0xE6);
    private static readonly SKColor RainDrop = new(0xD8, 0xEC, 0xFF, 0x9A);
    private static readonly SKColor SnowFlake = new(0xFF, 0xFF, 0xFF, 0xDC);

    private readonly ImageStore _images;
    private readonly bool _ownsImages;
    private readonly Lock _gate = new();
    private SKTypeface? _popTypeface;
    private bool _ownsTypeface = true;
    private BuiltinArt? _art;
    private bool _artSet;

    public SkiaWorldRenderer(ImageStore? images = null)
    {
        _ownsImages = images is null;
        _images = images ?? new ImageStore();
    }

    private enum DrawKind
    {
        Node,
        Machine,
        Crop,
        Item,
        Entity,
        Player,
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
    /// The built-in art pack used for anything the project binds no art to. Defaults to
    /// the embedded pack (<see cref="BuiltinArt.Default"/>); set to null to draw the
    /// original colored shapes only.
    /// </summary>
    public BuiltinArt? Art
    {
        get => _artSet ? _art : TryDefaultArt();
        set
        {
            _art = value;
            _artSet = true;
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

    private static BuiltinArt? TryDefaultArt()
    {
        try
        {
            return BuiltinArt.Default;
        }
#pragma warning disable CA1031 // A missing/corrupt embedded pack degrades to color blocks.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Trees and weeds take the seasonal foliage tint; rocks and ores don't.</summary>
    private static bool IsFoliage(BuiltinArtEntry? entry) =>
        entry is not null && (entry.Name.StartsWith("node-tree", StringComparison.Ordinal) || entry.Name.StartsWith("node-weeds", StringComparison.Ordinal));

    private void RenderCore(SKCanvas canvas, WorldSnapshot snapshot)
    {
        var art = Art;
        var pixelArt = snapshot.PixelArt != false;
        // Pixel art: nearest-neighbour sampling (canvas imageSmoothingEnabled = false).
        var filterQuality = pixelArt ? SKFilterQuality.None : SKFilterQuality.Medium;
        var ts = snapshot.TileSize;
        var padding = snapshot.Padding;
        var pitch = snapshot.Pitch;
        var camera = snapshot.Camera;
        var tick = snapshot.Tick;
        var atmosphere = snapshot.Atmosphere;
        var season = atmosphere?.Season;
        var foliageTint = Atmosphere.FoliageTint(season);
        var artScale = art is null ? 1 : ts / art.TileSize;
        var (viewWidth, viewHeight) = ViewportSize(snapshot);
        var (worldWidth, worldHeight) = snapshot.WorldPixelSize();

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        using var shape = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var imagePaint = new SKPaint { IsAntialias = false, FilterQuality = filterQuality };
        using var foliagePaint = new SKPaint { IsAntialias = false, FilterQuality = filterQuality };
        using var shadow = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ShadowColor, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.8, ts / 14)) };
        if (foliageTint is { } foliageColor)
        {
            foliagePaint.ColorFilter = SKColorFilter.CreateBlendMode(foliageColor, SKBlendMode.Modulate);
        }

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

        // Creator sprites: scaled to fit the box, bottom-aligned (TS drawSprite).
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

        // Built-in sprites: drawn at the pack's native scale (one sheet pixel = ts/32 world
        // pixels), centred on the tile, standing on its bottom edge (or filling it for ground tiles).
        bool DrawBuiltin(SnapshotSprite? sprite, double px, double py, double opacity = 1, bool foliage = false, bool top = false)
        {
            if (sprite is null)
            {
                return false;
            }

            var img = _images.Get(sprite.ImageUrl);
            if (img is null)
            {
                return false;
            }

            var sw = sprite.FrameWidth;
            var sh = sprite.FrameHeight;
            var sx = sprite.SourceX ?? 0;
            var sy = sprite.SourceY ?? 0;
            if (sw <= 0 || sh <= 0 || sx + sw > img.Width || sy + sh > img.Height)
            {
                return false;
            }

            var dw = sw * artScale;
            var dh = sh * artScale;
            var dest = SKRect.Create((float)(px + ((ts - dw) / 2)), (float)(top ? py : py + ts - dh), (float)dw, (float)dh);
            var paint = foliage && foliageTint is not null ? foliagePaint : imagePaint;
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * opacity));
            canvas.DrawImage(img, SKRect.Create((float)sx, (float)sy, (float)sw, (float)sh), dest, paint);
            return true;
        }

        void DrawWholeImage(SKImage img, double x, double y, double w, double h)
        {
            imagePaint.Color = SKColors.White;
            canvas.DrawImage(img, SKRect.Create(0, 0, img.Width, img.Height), SKRect.Create((float)x, (float)y, (float)w, (float)h), imagePaint);
        }

        void DrawShadow(double px, double py, double widthFactor)
        {
            var rx = (float)(ts * widthFactor / 2);
            var ry = (float)Math.Max(1.5, ts * 0.11);
            canvas.DrawOval((float)(px + (ts / 2)), (float)(py + ts - ry - 1), rx, ry, shadow);
        }

        // ---- Pass 1: ground -------------------------------------------------------
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
                    if (!DrawSprite(ArtLayer(tile, 0), px, py, ts, ts) && art is not null)
                    {
                        if (tile.Background == "soil")
                        {
                            // Farmable soil is drawn tilled; wet after watering, speckled when fertilized.
                            DrawBuiltin(art.Soil(tile.Watered, x, y), px, py, top: true);
                            if (tile.Fertilized)
                            {
                                DrawBuiltin(art.FertilizedMarker(), px, py, top: true);
                            }
                        }
                        else
                        {
                            DrawBuiltin(art.Tile(tile.Background, season, x, y, tick), px, py, top: true);
                        }
                    }

                    if (tile.Overlay is not null && !DrawSprite(ArtLayer(tile, 1), px, py, ts, ts) && !(art is not null && DrawBuiltin(art.Tile(tile.Overlay, null, x, y, tick), px, py, top: true)))
                    {
                        fill.Color = CssColor.WithOpacity(Canvas2d.TileColor(tile.Overlay, "path"), 0.7);
                        canvas.DrawRect(tileRect, fill);
                    }

                    if (tile.Object is not null && !DrawSprite(ArtLayer(tile, 2), px, py, ts, ts) && !(art is not null && DrawBuiltin(art.Tile(tile.Object, null, x, y, tick), px, py, top: true)))
                    {
                        fill.Color = Canvas2d.TileColor(tile.Object, "wall");
                        canvas.DrawRect(tileRect, fill);
                    }
                }

                if (tile.LadderDown && !(art is not null && DrawBuiltin(art.Ladder(), px, py, top: true)))
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
            }
        }

        // ---- Pass 2: collect everything that stands on the ground -------------------
        // One extra row below the viewport: tall objects there poke into view.
        var drawables = new List<Drawable>();
        var objectsEnd = Math.Min(snapshot.Height - 1, yEnd + 1);
        for (var y = yStart; y <= objectsEnd && y < snapshot.Tiles.Count; y++)
        {
            var row = snapshot.Tiles[y];
            for (var x = xStart; x <= xEnd && x < row.Count; x++)
            {
                var tile = row[x];
                var px = padding + (x * pitch);
                var py = padding + (y * pitch);
                if (tile.Node is not null)
                {
                    drawables.Add(new Drawable(py + ts, drawables.Count, DrawKind.Node, tile, null, x, y, px, py));
                }

                if (tile.Machine is not null)
                {
                    drawables.Add(new Drawable(py + ts, drawables.Count, DrawKind.Machine, tile, null, x, y, px, py));
                }

                if (tile.Crop is not null)
                {
                    drawables.Add(new Drawable(py + ts, drawables.Count, DrawKind.Crop, tile, null, x, y, px, py));
                }

                if (tile.Item is not null)
                {
                    drawables.Add(new Drawable(py + ts, drawables.Count, DrawKind.Item, tile, null, x, y, px, py));
                }
            }
        }

        foreach (var npc in snapshot.Npcs)
        {
            var px = Canvas2d.EntityPixelOrigin(npc.X, padding, pitch);
            var py = Canvas2d.EntityPixelOrigin(npc.Y, padding, pitch);
            drawables.Add(new Drawable(py + ts, drawables.Count, DrawKind.Entity, null, npc, 0, 0, px, py));
        }

        var player = snapshot.Player;
        var playerX = player.PixelX ?? Canvas2d.EntityPixelOrigin(player.X, padding, pitch);
        var playerY = player.PixelY ?? Canvas2d.EntityPixelOrigin(player.Y, padding, pitch);
        drawables.Add(new Drawable(playerY + ts, drawables.Count, DrawKind.Player, null, player, 0, 0, playerX, playerY));

        // Y-sort: lower on screen draws later (in front); ties keep insertion order.
        drawables.Sort(static (a, b) => a.SortY != b.SortY ? a.SortY.CompareTo(b.SortY) : a.Seq.CompareTo(b.Seq));

        // ---- Pass 3: soft drop shadows (built-in art only; the color-block look stays as it was)
        if (art is not null)
        {
            foreach (var d in drawables)
            {
                switch (d.Kind)
                {
                    case DrawKind.Node when d.Tile!.Node is { Sprite: null } node && !node.Depleted:
                        DrawShadow(d.Px, d.Py, IsFoliage(art.NodeEntry(node.TypeId)) ? 0.7 : 0.6);
                        break;
                    case DrawKind.Machine when d.Tile!.Machine is { Sprite: null }:
                        DrawShadow(d.Px, d.Py, 0.7);
                        break;
                    case DrawKind.Item when d.Tile!.Item is { Sprite: null, ImageUrl: null }:
                        DrawShadow(d.Px, d.Py, 0.4);
                        break;
                    case DrawKind.Entity when d.Entity is { Sprite: null, ImageUrl: null }:
                    case DrawKind.Player when d.Entity is { Sprite: null, ImageUrl: null }:
                        DrawShadow(d.Px, d.Py, 0.55);
                        break;
                }
            }
        }

        // ---- Pass 4: y-sorted objects and entities ----------------------------------
        foreach (var d in drawables)
        {
            var px = d.Px;
            var py = d.Py;
            switch (d.Kind)
            {
                case DrawKind.Node:
                {
                    var node = d.Tile!.Node!;
                    var opacity = node.Depleted ? 0.35 : 1;
                    if (DrawSprite(node.Sprite, px, py, ts, ts, opacity))
                    {
                        break;
                    }

                    if (art is not null && DrawBuiltin(art.Node(node.TypeId, d.TileX, d.TileY), px, py, opacity, IsFoliage(art.NodeEntry(node.TypeId))))
                    {
                        break;
                    }

                    // Gathering node: filled circle in the node color; faded while depleted.
                    var radius = (float)Math.Floor(ts * 0.32);
                    var cx = (float)(px + (ts / 2));
                    var cy = (float)(py + (ts / 2));
                    shape.Color = CssColor.WithOpacity(CssColor.Parse(node.Color), opacity);
                    canvas.DrawCircle(cx, cy, radius, shape);
                    stroke.Color = CssColor.WithOpacity(CssColor.Parse("#00000040"), opacity);
                    stroke.StrokeWidth = 1;
                    canvas.DrawCircle(cx, cy, radius, stroke);
                    break;
                }

                case DrawKind.Machine:
                {
                    var machine = d.Tile!.Machine!;
                    if (DrawSprite(machine.Sprite, px, py, ts, ts))
                    {
                        break;
                    }

                    if (art is not null && DrawBuiltin(art.Machine(machine.TypeId, machine.Working), px, py))
                    {
                        if (machine.OutputReady)
                        {
                            DrawBuiltin(art.ReadyMarker(), px, py - (ts * 0.45), top: true);
                        }

                        break;
                    }

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

                    break;
                }

                case DrawKind.Crop:
                {
                    var crop = d.Tile!.Crop!;
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

                    if (DrawSprite(crop.Sprite, px, py, ts, ts))
                    {
                        break;
                    }

                    if (art is not null && DrawBuiltin(art.Crop(crop.CropId, crop.ColorIndex, crop.Stages, crop.Withered, crop.Mature), px, py))
                    {
                        break;
                    }

                    var colorIndex = Math.Clamp(crop.ColorIndex, 0, Canvas2d.CropStageColors.Count - 1);
                    fill.Color = CssColor.Parse(crop.Withered ? Canvas2d.WitheredCropColor : Canvas2d.CropStageColors[colorIndex]);
                    canvas.DrawRect(cropRect, fill);
                    break;
                }

                case DrawKind.Item:
                {
                    var item = d.Tile!.Item!;
                    if (DrawSprite(item.Sprite, px, py, ts, ts))
                    {
                        break;
                    }

                    if (item.ImageUrl is not null)
                    {
                        if (_images.Get(item.ImageUrl) is { Width: > 0 } img)
                        {
                            var itemSize = Math.Floor(ts * 0.5);
                            DrawWholeImage(img, px + ((ts - itemSize) / 2), py + ((ts - itemSize) / 2), itemSize, itemSize);
                        }

                        break;
                    }

                    if (art is not null && DrawBuiltin(art.Item(item.ItemType), px, py))
                    {
                        break;
                    }

                    shape.Color = CssColor.Parse(Canvas2d.ItemColor);
                    canvas.DrawCircle((float)(px + (ts / 2)), (float)(py + (ts / 2)), (float)Math.Floor(ts * 0.15), shape);
                    break;
                }

                case DrawKind.Entity:
                {
                    var npc = d.Entity!;
                    var npcW = Math.Floor(ts * 0.65);
                    var npcH = Math.Floor(ts * 0.75);
                    if (npc.Sprite is not null && DrawSprite(npc.Sprite, px, py, ts, ts))
                    {
                        break;
                    }

                    if (npc.ImageUrl is not null && _images.Get(npc.ImageUrl) is { Width: > 0 } npcImage)
                    {
                        DrawWholeImage(npcImage, px + ((ts - npcW) / 2), py + ((ts - npcH) / 2), npcW, npcH);
                        break;
                    }

                    if (art is not null)
                    {
                        var builtin = npc.Kind == "animal"
                            ? art.Animal(npc.SpeciesId, npc.Direction, tick, npc.Moving)
                            : art.Npc(npc.Appearance, npc.Direction, tick, npc.Moving);
                        if (DrawBuiltin(builtin, px, py))
                        {
                            break;
                        }
                    }

                    var body = new SKRoundRect(SKRect.Create((float)(px + ((ts - npcW) / 2)), (float)(py + ((ts - npcH) / 2)), (float)npcW, (float)npcH), 2);
                    shape.Color = CssColor.Parse(npc.Color ?? Canvas2d.NpcColor);
                    canvas.DrawRoundRect(body, shape);
                    stroke.Color = CssColor.Parse(Canvas2d.NpcBorderColor);
                    stroke.StrokeWidth = 1;
                    canvas.DrawRoundRect(body, stroke);
                    break;
                }

                case DrawKind.Player:
                {
                    var playerW = Math.Floor(ts * 0.65);
                    var playerH = Math.Floor(ts * 0.75);
                    if (player.Sprite is not null && DrawSprite(player.Sprite, px, py, ts, ts))
                    {
                        break;
                    }

                    if (player.ImageUrl is not null && _images.Get(player.ImageUrl) is { Width: > 0 } playerImage)
                    {
                        DrawWholeImage(playerImage, px + ((ts - playerW) / 2), py + ((ts - playerH) / 2), playerW, playerH);
                        break;
                    }

                    if (art is not null && DrawBuiltin(art.Player(player.Direction, tick, player.Moving), px, py))
                    {
                        break;
                    }

                    var body = new SKRoundRect(SKRect.Create((float)(px + ((ts - playerW) / 2)), (float)(py + ((ts - playerH) / 2)), (float)playerW, (float)playerH), 2);
                    shape.Color = CssColor.Parse(Canvas2d.PlayerColor);
                    canvas.DrawRoundRect(body, shape);
                    stroke.Color = CssColor.Parse(Canvas2d.PlayerBorderColor);
                    stroke.StrokeWidth = 2;
                    canvas.DrawRoundRect(body, stroke);

                    var (dx, dy) = Canvas2d.DirectionOffset(player.Direction);
                    var dotSize = Math.Floor(ts * 0.1);
                    var centerX = px + (ts / 2);
                    var centerY = py + (ts / 2);
                    var dotX = centerX + (dx * ((playerW / 2) - (dotSize / 2))) - (dotSize / 2);
                    var dotY = centerY + (dy * ((playerH / 2) - (dotSize / 2))) - (dotSize / 2);
                    shape.Color = CssColor.Parse("#ffffffcc");
                    canvas.DrawCircle((float)(dotX + (dotSize / 2)), (float)(dotY + (dotSize / 2)), (float)(dotSize / 2), shape);
                    break;
                }
            }
        }

        // ---- Pass 5: atmosphere ------------------------------------------------------
        if (atmosphere is not null)
        {
            var worldRect = SKRect.Create(0, 0, (float)worldWidth, (float)worldHeight);
            var overlay = Atmosphere.WeatherOverlay(atmosphere.WeatherId, atmosphere.WeatherOverlay);
            var tint = Atmosphere.DaylightTint(atmosphere.TimeMinutes);
            if (overlay == "rain")
            {
                tint = new SKColor((byte)(tint.Red * RainTint.Red / 255), (byte)(tint.Green * RainTint.Green / 255), (byte)(tint.Blue * RainTint.Blue / 255));
            }

            if (!Atmosphere.IsNeutral(tint))
            {
                // Modulate multiplies color (and alpha) so the padding/letterbox stays untouched.
                using var tintPaint = new SKPaint { Color = tint, BlendMode = SKBlendMode.Modulate, IsAntialias = false };
                canvas.DrawRect(worldRect, tintPaint);
            }

            if (overlay is not null)
            {
                DrawWeather(canvas, overlay, tick, camera?.X ?? 0, camera?.Y ?? 0, viewWidth, viewHeight, worldRect, shape, stroke);
            }
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

    /// <summary>
    /// Rain streaks or snow flakes: every particle's position is a pure function of the
    /// tick and its index (hash), so frames are reproducible and nothing is stored.
    /// </summary>
    private static void DrawWeather(SKCanvas canvas, string overlay, double tick, double originX, double originY, double viewWidth, double viewHeight, SKRect worldRect, SKPaint shape, SKPaint stroke)
    {
        var count = Atmosphere.ParticleCount(viewWidth, viewHeight);
        var t = Math.Max(0, tick);
        canvas.Save();
        canvas.ClipRect(worldRect);
        if (overlay == "rain")
        {
            stroke.Color = RainDrop;
            stroke.StrokeWidth = 1;
            for (var i = 0; i < count; i++)
            {
                var index = (uint)i;
                var speed = 9 + (Atmosphere.Unit(index, 3) * 4);
                var x = originX + ((((Atmosphere.Unit(index, 1) * viewWidth) - (t * 2.5)) % viewWidth + viewWidth) % viewWidth);
                var y = originY + (((Atmosphere.Unit(index, 2) * viewHeight) + (t * speed)) % viewHeight);
                canvas.DrawLine((float)x, (float)y, (float)(x - 2), (float)(y - 9), stroke);
            }
        }
        else
        {
            shape.Color = SnowFlake;
            for (var i = 0; i < count; i++)
            {
                var index = (uint)i;
                var speed = 0.9 + (Atmosphere.Unit(index, 3) * 0.8);
                var sway = Math.Sin((t / 12) + (Atmosphere.Unit(index, 4) * 6.283)) * 5;
                var x = originX + ((((Atmosphere.Unit(index, 1) * viewWidth) + sway) % viewWidth + viewWidth) % viewWidth);
                var y = originY + (((Atmosphere.Unit(index, 2) * viewHeight) + (t * speed)) % viewHeight);
                canvas.DrawCircle((float)x, (float)y, (float)(1 + (Atmosphere.Unit(index, 5) * 1.2)), shape);
            }
        }

        canvas.Restore();
    }

    private static SnapshotSprite? ArtLayer(SnapshotTile tile, int index) =>
        tile.ArtLayers is { } layers && index < layers.Length ? layers[index] : null;

    /// <summary>One thing to draw in the y-sorted pass.</summary>
    private readonly record struct Drawable(double SortY, int Seq, DrawKind Kind, SnapshotTile? Tile, SnapshotEntity? Entity, int TileX, int TileY, double Px, double Py);
}
