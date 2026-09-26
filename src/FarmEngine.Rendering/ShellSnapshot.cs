using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmEngine.Rendering;

/// <summary>Options for <see cref="ShellSnapshot.BuildShellSnapshot"/>.</summary>
public sealed record ShellSnapshotOptions(
    double TileSize,
    double Padding,
    double? PixelX = null,
    double? PixelY = null,
    SnapshotCamera? Camera = null);

/// <summary>
/// Port of packages/game-shell/src/snapshot.ts: builds a plain-data <see cref="WorldSnapshot"/>
/// from engine content + live state (play mode). Plus <see cref="BuildEditorSnapshot"/>, the
/// edit-mode counterpart from the web <c>GameView</c> (project data, grid seams + overlay).
/// </summary>
public static class ShellSnapshot
{
    public static WorldSnapshot BuildShellSnapshot(GameContent content, GameState state, Scene scene, ShellSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);

        var nodeTypes = ById(content.NodeTypes, def => def.Id);
        var machineTypes = ById(content.MachineTypes, def => def.Id);
        var animalSpecies = ById(content.AnimalSpecies, def => def.Id);
        var sceneNpcs = content.Npcs
            .Where(npc => (state.Npcs.TryGetValue(npc.Id, out var live) && live is not null ? live.SceneId : npc.SceneId) == scene.Id)
            .ToList();

        var tiles = BuildTiles(scene, content.Crops, nodeTypes, machineTypes, hideItemAt: null);

        var npcs = new List<SnapshotEntity>();
        foreach (var npc in sceneNpcs)
        {
            var live = state.Npcs.TryGetValue(npc.Id, out var s) ? s : null;
            var x = live?.X ?? npc.X;
            var y = live?.Y ?? npc.Y;
            // A scheduled/patrolling NPC with path steps left is walking toward the next step.
            var next = live?.Path is { Count: > 0 } path ? path[0] : null;
            npcs.Add(new SnapshotEntity
            {
                X = x,
                Y = y,
                ImageUrl = npc.CustomImage,
                Appearance = npc.Appearance,
                Direction = next is null ? "down" : DirectionToward(x, y, next.X, next.Y),
                Moving = next is not null,
            });
        }

        foreach (var animal in state.Animals.Where(animal => animal.SceneId == scene.Id))
        {
            npcs.Add(new SnapshotEntity
            {
                X = animal.X,
                Y = animal.Y,
                Color = animalSpecies.TryGetValue(animal.SpeciesId, out var species) ? species.Color : null,
                Kind = "animal",
                SpeciesId = animal.SpeciesId,
                // Animals face a stable direction chosen from their id so a herd isn't uniform.
                Direction = Directions.All[(int)(BuiltinArt.Hash(animal.Id.Length, (int)(animal.X + animal.Y)) % 4)],
            });
        }

        var clock = state.Clock;
        return new WorldSnapshot
        {
            Width = (int)scene.Width,
            Height = (int)scene.Height,
            TileSize = options.TileSize,
            Padding = options.Padding,
            // Play mode renders a contiguous world (no editor grid seams).
            TileGap = 0,
            Camera = options.Camera,
            GridOverlay = false,
            Tick = clock.Tick,
            Atmosphere = new SnapshotAtmosphere(clock.TimeMinutes, clock.WeatherId, clock.Season)
            {
                WeatherOverlay = content.Weather.Types.FirstOrDefault(w => w.Id == clock.WeatherId)?.Overlay,
            },
            Tiles = tiles,
            Npcs = npcs,
            Player = new SnapshotPlayer
            {
                X = state.Player.X,
                Y = state.Player.Y,
                Direction = state.Player.Direction,
                PixelX = options.PixelX,
                PixelY = options.PixelY,
                Moving = state.Player.MoveIntent.Dx != 0 || state.Player.MoveIntent.Dy != 0,
            },
        };
    }

    private static string DirectionToward(double x, double y, double targetX, double targetY)
    {
        var dx = targetX - x;
        var dy = targetY - y;
        if (Math.Abs(dx) > Math.Abs(dy))
        {
            return dx < 0 ? Directions.Left : Directions.Right;
        }

        return dy < 0 ? Directions.Up : Directions.Down;
    }

    /// <summary>
    /// Edit-mode snapshot (web <c>GameView.buildSnapshot</c> outside play): the authored
    /// project scene with 1px grid seams, the grid overlay, NPCs at their authored positions,
    /// and items hidden under the player/NPCs. <paramref name="content"/> supplies the merged
    /// node/machine/crop definitions (<c>EngineState.CreateContentFromProject</c>).
    /// </summary>
    public static WorldSnapshot BuildEditorSnapshot(GameProject project, GameContent content, Scene scene, double tileSize = 28, double padding = 12)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(scene);

        var nodeTypes = ById(content.NodeTypes, def => def.Id);
        var machineTypes = ById(content.MachineTypes, def => def.Id);
        var animalSpecies = ById(content.AnimalSpecies, def => def.Id);
        var sceneNpcs = (project.Npcs ?? []).Where(npc => npc.SceneId == scene.Id).ToList();

        bool Occupied(int x, int y) =>
            (project.Player.SceneId == scene.Id && Math.Floor(project.Player.X) == x && Math.Floor(project.Player.Y) == y)
            || sceneNpcs.Any(npc => npc.X == x && npc.Y == y);

        var tiles = BuildTiles(scene, content.Crops, nodeTypes, machineTypes, Occupied);

        var npcs = new List<SnapshotEntity>();
        npcs.AddRange(sceneNpcs.Select(npc => new SnapshotEntity { X = npc.X, Y = npc.Y, ImageUrl = npc.CustomImage, Appearance = npc.Appearance }));
        npcs.AddRange((project.Animals ?? [])
            .Where(animal => animal.SceneId == scene.Id)
            .Select(animal => new SnapshotEntity
            {
                X = animal.X,
                Y = animal.Y,
                Color = animalSpecies.TryGetValue(animal.SpeciesId, out var species) ? species.Color : null,
                Kind = "animal",
                SpeciesId = animal.SpeciesId,
            }));

        var snapshot = new WorldSnapshot
        {
            Width = (int)scene.Width,
            Height = (int)scene.Height,
            TileSize = tileSize,
            Padding = padding,
            TileGap = 1,
            GridOverlay = true,
            Tiles = tiles,
            Npcs = npcs,
            Player = new SnapshotPlayer
            {
                X = project.Player.X,
                Y = project.Player.Y,
                Direction = string.IsNullOrEmpty(project.Player.Direction) ? "down" : project.Player.Direction,
                ImageUrl = project.PlayerCustomImage,
            },
        };

        // The player marker only belongs on its own scene.
        if (project.Player.SceneId != scene.Id)
        {
            snapshot.Player.X = -1000;
            snapshot.Player.Y = -1000;
        }

        return snapshot;
    }

    private static List<List<SnapshotTile>> BuildTiles(
        Scene scene,
        OrderedDictionary<string, CropDefinition> crops,
        Dictionary<string, NodeTypeDefinition> nodeTypes,
        Dictionary<string, MachineTypeDefinition> machineTypes,
        Func<int, int, bool>? hideItemAt)
    {
        var tiles = new List<List<SnapshotTile>>();
        for (var y = 0; y < scene.Height; y++)
        {
            var row = new List<SnapshotTile>();
            var sourceRow = y < scene.Tiles.Count ? scene.Tiles[y] : [];
            for (var x = 0; x < scene.Width; x++)
            {
                var tile = x < sourceRow.Count ? sourceRow[x] : Tiles.CreateEmptyTile(x, y);
                var background = string.IsNullOrEmpty(tile.Background) ? tile.Type : tile.Background;
                var snapshotTile = new SnapshotTile
                {
                    Background = background,
                    Overlay = tile.Overlay,
                    Object = tile.Object,
                    ImageUrl = tile.CustomImage,
                    // Soil state (read only): plantable soil is drawn tilled; wet after watering
                    // or rain (moisture), speckled once fertilized.
                    Tilled = background == TileTypes.Soil,
                    Watered = background == TileTypes.Soil && (tile.SoilState == SoilStates.Watered || tile.SoilMoisture > 0),
                    Fertilized = background == TileTypes.Soil && (tile.SoilState == SoilStates.Fertilized || tile.SoilFertility > 0),
                };

                if (tile.Crop is { } crop && crops.TryGetValue(crop.Type, out var definition) && definition is not null)
                {
                    snapshotTile.Crop = new SnapshotCrop
                    {
                        ColorIndex = (int)Math.Max(0, crop.Stage),
                        Mature = Crops.IsCropMatureByDays(crop, definition),
                        Withered = crop.Withered == true,
                        CropId = crop.Type,
                        Stages = (int)Math.Max(0, definition.Stages),
                    };
                }

                if (tile.Node is { } node)
                {
                    snapshotTile.Node = new SnapshotNode
                    {
                        Color = nodeTypes.TryGetValue(node.TypeId, out var nodeDef) ? nodeDef.Color : "#7a5a3a",
                        Depleted = node.RemainingHealth <= 0,
                        TypeId = node.TypeId,
                    };
                }

                if (tile.Machine is { } machine)
                {
                    snapshotTile.Machine = new SnapshotMachine
                    {
                        Color = machineTypes.TryGetValue(machine.TypeId, out var machineDef) ? machineDef.Color : "#9a7b4f",
                        Working = machine.Processing is not null,
                        OutputReady = machine.Output is { Count: > 0 },
                        TypeId = machine.TypeId,
                    };
                }

                if (tile.LadderDown == true)
                {
                    snapshotTile.LadderDown = true;
                }

                if (tile.Item is { } item && hideItemAt?.Invoke(x, y) != true)
                {
                    snapshotTile.Item = new SnapshotItem { ImageUrl = item.CustomImage, ItemType = item.Type };
                }

                row.Add(snapshotTile);
            }

            tiles.Add(row);
        }

        return tiles;
    }

    private static Dictionary<string, T> ById<T>(IEnumerable<T> definitions, Func<T, string> id)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var def in definitions)
        {
            map[id(def)] = def; // later definitions win, like `new Map(entries)`
        }

        return map;
    }
}
