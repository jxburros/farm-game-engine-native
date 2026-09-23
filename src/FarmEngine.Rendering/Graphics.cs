using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmEngine.Rendering;

/// <summary>
/// The subset of a project the graphics pipeline reads (TS <c>GraphicsSource</c>): custom
/// assets, player art, and the definitions that carry <see cref="VisualRef"/>s.
/// </summary>
public sealed record GraphicsSource
{
    public List<CustomAsset> CustomAssets { get; init; } = [];

    public VisualRef? PlayerVisual { get; init; }

    public string? PlayerCustomImage { get; init; }

    public GraphicsSettings? Graphics { get; init; }

    public List<Npc>? Npcs { get; init; }

    public List<AnimalSpeciesDefinition>? AnimalSpecies { get; init; }

    public List<AnimalState>? Animals { get; init; }

    public List<NodeTypeDefinition>? NodeTypes { get; init; }

    public List<MachineTypeDefinition>? MachineTypes { get; init; }

    public List<CustomCropDefinition>? CustomCrops { get; init; }

    /// <summary>Everything straight from the project (edit mode, where the project is the live state).</summary>
    public static GraphicsSource FromProject(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new GraphicsSource
        {
            CustomAssets = project.CustomAssets ?? [],
            PlayerVisual = project.PlayerVisual,
            PlayerCustomImage = project.PlayerCustomImage,
            Graphics = project.Graphics,
            Npcs = project.Npcs,
            AnimalSpecies = project.AnimalSpecies,
            Animals = project.Animals,
            NodeTypes = project.NodeTypes,
            MachineTypes = project.MachineTypes,
            CustomCrops = project.CustomCrops,
        };
    }

    /// <summary>
    /// Play mode (game-shell <c>draw()</c>): project art + live NPC/animal positions from the
    /// state and the merged content definitions.
    /// </summary>
    public static GraphicsSource FromState(GameProject project, GameContent content, GameState state)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(state);
        return FromProject(project) with
        {
            Npcs = content.Npcs.Select(npc => state.Npcs.TryGetValue(npc.Id, out var live) && live is not null
                ? npc with { X = live.X, Y = live.Y, SceneId = live.SceneId }
                : npc).ToList(),
            NodeTypes = content.NodeTypes,
            MachineTypes = content.MachineTypes,
            AnimalSpecies = content.AnimalSpecies,
            Animals = state.Animals,
        };
    }
}

/// <summary>Port of packages/renderer-canvas2d/src/graphics.ts.</summary>
public static class Graphics
{
    private static readonly Dictionary<string, int> DirectionRows = new(StringComparer.Ordinal)
    {
        ["down"] = 0,
        ["left"] = 1,
        ["right"] = 2,
        ["up"] = 3,
    };

    /// <summary>Pure frame selection, shared by preview and exported games.</summary>
    public static SnapshotSprite? ResolveVisual(
        IReadOnlyList<CustomAsset> assets,
        VisualRef? visual,
        double tick,
        Func<string?, string?>? resolve = null,
        string direction = "down",
        bool moving = true)
    {
        ArgumentNullException.ThrowIfNull(assets);
        resolve ??= static url => url;
        if (visual is null)
        {
            return null;
        }

        var asset = assets.FirstOrDefault(a => a.Id == visual.AssetId);
        if (asset is null)
        {
            return null;
        }

        var source = asset;
        var frame = visual.Frame;
        List<string> preferred = visual.Animation is not null
            ? [visual.Animation]
            : moving
                ? [$"walk-{direction}", "walk", $"idle-{direction}", "idle"]
                : [$"idle-{direction}", "idle"];
        var clip = preferred
            .Select(name => asset.Animations?.FirstOrDefault(c => c.Name == name))
            .FirstOrDefault(c => c is not null) ?? asset.Animations?.FirstOrDefault();
        if (frame is null && clip is { Frames.Count: > 0 })
        {
            var duration = clip.Frames.Sum(f => f.Ticks);
            var time = clip.Loop ? JsMod(Math.Max(0, tick), duration) : Math.Min(Math.Max(0, tick), duration - 1);
            frame = clip.Frames[^1];
            foreach (var candidate in clip.Frames)
            {
                if (time < candidate.Ticks)
                {
                    frame = candidate;
                    break;
                }

                time -= candidate.Ticks;
            }
        }

        if (frame?.AssetId is { } frameAssetId)
        {
            var frameAsset = assets.FirstOrDefault(a => a.Id == frameAssetId);
            if (frameAsset is null)
            {
                return null;
            }

            source = frameAsset;
        }

        var imageUrl = resolve(source.DataUrl);
        if (string.IsNullOrEmpty(imageUrl))
        {
            return null;
        }

        if (frame is not null)
        {
            return new SnapshotSprite
            {
                ImageUrl = imageUrl,
                FrameWidth = frame.Width,
                FrameHeight = frame.Height,
                Frame = 0,
                Row = 0,
                SourceX = frame.X,
                SourceY = frame.Y,
            };
        }

        if (asset.Sheet is { } sheet)
        {
            return new SnapshotSprite
            {
                ImageUrl = imageUrl,
                FrameWidth = sheet.FrameWidth,
                FrameHeight = sheet.FrameHeight,
                Frame = moving && sheet.TicksPerFrame > 0 && sheet.Frames > 0 ? JsMod(Math.Floor(tick / sheet.TicksPerFrame), sheet.Frames) : 0,
                Row = sheet.Directional ? DirectionRows.GetValueOrDefault(direction, 0) : 0,
            };
        }

        // Zero sizes mean a full image; actual dimensions become available at draw time.
        return new SnapshotSprite { ImageUrl = imageUrl, FrameWidth = asset.Width ?? 0, FrameHeight = asset.Height ?? 0, Frame = 0, Row = 0 };
    }

    /// <summary>Decorates the existing simulation snapshot without changing any gameplay rules.</summary>
    public static WorldSnapshot ApplyGraphics(
        WorldSnapshot snapshot,
        GraphicsSource source,
        Scene scene,
        double tick,
        bool moving,
        Func<string?, string?>? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scene);
        resolve ??= static url => url;
        var assets = source.CustomAssets;

        VisualRef? Legacy(string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            var asset = assets.FirstOrDefault(a => a.DataUrl == url || resolve(a.DataUrl) == resolve(url));
            return asset is null ? null : new VisualRef { AssetId = asset.Id };
        }

        SnapshotSprite? Art(VisualRef? visual) => ResolveVisual(assets, visual, tick, resolve);

        snapshot.PixelArt = source.Graphics?.PixelArt ?? true;
        snapshot.Player.ImageUrl = resolve(source.PlayerCustomImage);
        snapshot.Player.Sprite = ResolveVisual(assets, source.PlayerVisual ?? Legacy(source.PlayerCustomImage), tick, resolve, snapshot.Player.Direction, moving);

        var npcs = (source.Npcs ?? []).Where(n => n.SceneId == scene.Id).ToList();
        for (var i = 0; i < npcs.Count; i++)
        {
            if (i < snapshot.Npcs.Count)
            {
                snapshot.Npcs[i].Sprite = Art(npcs[i].Visual ?? Legacy(npcs[i].CustomImage));
            }
        }

        var animals = (source.Animals ?? []).Where(a => a.SceneId == scene.Id).ToList();
        for (var i = 0; i < animals.Count; i++)
        {
            var index = npcs.Count + i;
            if (index < snapshot.Npcs.Count)
            {
                snapshot.Npcs[index].Sprite = Art(source.AnimalSpecies?.FirstOrDefault(s => s.Id == animals[i].SpeciesId)?.Visual);
            }
        }

        for (var y = 0; y < scene.Tiles.Count; y++)
        {
            var row = scene.Tiles[y];
            for (var x = 0; x < row.Count; x++)
            {
                var tile = row[x];
                if (y >= snapshot.Tiles.Count || x >= snapshot.Tiles[y].Count)
                {
                    continue;
                }

                var target = snapshot.Tiles[y][x];
                target.ArtLayers =
                [
                    LayerArt(tile.Background, tile.Visuals?.Background),
                    LayerArt(tile.Overlay, tile.Visuals?.Overlay),
                    LayerArt(tile.Object, tile.Visuals?.Object),
                ];

                if (target.Crop is not null && tile.Crop is not null)
                {
                    var crop = source.CustomCrops?.FirstOrDefault(c => c.Id == tile.Crop.Type);
                    var visual = crop?.Visual ?? Legacy(crop?.CustomAsset);
                    var asset = assets.FirstOrDefault(a => a.Id == visual?.AssetId);
                    var growth = asset?.Animations?.FirstOrDefault(c => c.Name == "growth");
                    target.Crop.Sprite = Art(growth is { Frames.Count: > 0 } && visual is not null
                        ? visual with { Frame = growth.Frames[(int)Math.Min(Math.Max(0, tile.Crop.Stage), growth.Frames.Count - 1)] }
                        : visual);
                }

                if (target.Node is not null)
                {
                    target.Node.Sprite = Art(source.NodeTypes?.FirstOrDefault(n => n.Id == tile.Node?.TypeId)?.Visual);
                }

                if (target.Machine is not null)
                {
                    target.Machine.Sprite = Art(source.MachineTypes?.FirstOrDefault(m => m.Id == tile.Machine?.TypeId)?.Visual);
                }

                if (target.Item is not null)
                {
                    target.Item.Sprite = Art(tile.Item?.Visual ?? Legacy(tile.Item?.CustomImage));
                }
            }
        }

        return snapshot;

        SnapshotSprite? LayerArt(string? type, VisualRef? visual)
        {
            var fallback = type is null ? null : assets.FirstOrDefault(a => a.Type == "tile" && a.TileType == type);
            return Art(visual ?? (fallback is not null ? new VisualRef { AssetId = fallback.Id } : null));
        }
    }

    /// <summary>JS <c>%</c> on non-negative operands (NaN-safe for zero divisors).</summary>
    private static double JsMod(double x, double y) => y == 0 ? double.NaN : x % y;
}
