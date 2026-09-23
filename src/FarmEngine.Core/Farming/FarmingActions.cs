using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Farming &amp; tool interactions (M2): energy costs, day-based growth,
/// gathering-node strikes, tiered area-of-effect, explicit selling (harvest
/// no longer auto-pays). (Port of engine-core/src/farming/actions.ts.)
/// </summary>
public static class FarmingActions
{
    private sealed record FacingTileResult(Scene Scene, double X, double Y, Tile Tile);

    private static FacingTileResult? FacingTile(GameState state)
    {
        var scene = WorldMovement.FindScene(state, state.Player.SceneId);
        if (scene is null) return null;
        var vector = WorldMovement.GetDirectionVector(state.Player.Direction);
        var origin = WorldMovement.PlayerTile(state);
        var x = origin.X + vector.Dx;
        var y = origin.Y + vector.Dy;
        if (x < 0 || x >= scene.Width || y < 0 || y >= scene.Height) return null;
        return new FacingTileResult(scene, x, y, scene.Tiles[(int)y][(int)x]);
    }

    /// <summary>Tiles affected by an AoE-capable tool: facing tile + perpendicular neighbors at tier 2+.</summary>
    private static List<(double X, double Y)> AoeTargets(Scene scene, double x, double y, string direction, double tier)
    {
        var targets = new List<(double X, double Y)> { (x, y) };
        if (tier >= 2)
        {
            var horizontal = direction == "up" || direction == "down";
            (double Dx, double Dy)[] offsets = horizontal ? [(-1, 0), (1, 0)] : [(0, -1), (0, 1)];
            foreach (var (dx, dy) in offsets)
            {
                var tx = x + dx;
                var ty = y + dy;
                if (tx >= 0 && tx < scene.Width && ty >= 0 && ty < scene.Height)
                {
                    targets.Add((tx, ty));
                }
            }
        }
        return targets;
    }

    /// <summary>TS <c>waterTileInPlace</c>: returns the watered tile (records are immutable).</summary>
    private static Tile WaterTile(Tile tile, double day)
    {
        if (tile.Background != TileTypes.Soil) return tile;
        tile = tile with
        {
            SoilMoisture = 100,
            SoilState = tile.SoilState == SoilStates.Fertilized ? SoilStates.Fertilized : SoilStates.Watered,
        };
        if (tile.Crop is not null && tile.Crop.Withered != true)
        {
            tile = tile with { Crop = tile.Crop with { Watered = true, LastWateredDay = day, DaysWithoutWater = 0 } };
        }
        return tile;
    }

    public static EngineStep HandleUseTool(EngineContext ctx, GameState state, string toolType)
    {
        var toolSlot = Inventory.FindToolSlot(state.Player.Inventory, toolType);
        if (toolSlot is null)
        {
            var name = toolType == ToolTypes.WateringCan ? "watering can" : Gathering.ReplaceFirstDash(toolType);
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"You need a {name}!"));
        }
        if (Tools.IsToolBroken(toolSlot.Item))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"Your {toolSlot.Item.Name} is broken! A shop can repair it."));
        }

        var target = FacingTile(state);
        if (target is null) return EngineStep.Of(state);
        var (scene, x, y, tile) = target;

        var tier = toolSlot.Item.ToolTier ?? 1;
        var power = toolSlot.Item.ToolPower ?? tier;
        var definition = Tools.GetToolDefinition(toolType);
        var energyCost = Energy.EffectiveEnergyCost(definition, tier);

        EngineStep Finish(GameState worked, List<Effect> effects)
        {
            // Durability + energy apply after a successful action.
            var inventory = Inventory.ReplaceItem(worked.Player.Inventory, toolSlot.Item.Id, Tools.DamageToolDurability(toolSlot.Item, 1));
            var withDurability = worked with { Player = worked.Player with { Inventory = inventory } };
            var spent = Energy.SpendEnergy(ctx, withDurability, energyCost);
            return new EngineStep(spent.State, [.. effects, .. spent.Effects]);
        }

        // Gathering node strike takes priority on node tiles.
        if (tile.Node is not null && Gathering.IsNodeActive(tile))
        {
            var outcome = Gathering.StrikeNode(ctx, state, scene.Id, x, y, toolType, tier, power);
            if (!outcome.Struck) return new EngineStep(outcome.State, outcome.Effects);
            return Finish(outcome.State, outcome.Effects);
        }

        if (toolType == ToolTypes.WateringCan && tile.Background == TileTypes.Soil)
        {
            var scenes = new List<Scene>(state.World.Scenes);
            var sceneIndex = scenes.FindIndex(s => s.Id == scene.Id);
            if (sceneIndex == -1) return EngineStep.Of(state);
            var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
            foreach (var spot in AoeTargets(scene, x, y, state.Player.Direction, tier))
            {
                tiles[(int)spot.Y][(int)spot.X] = WaterTile(tiles[(int)spot.Y][(int)spot.X], state.Clock.Day);
            }
            scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };
            return Finish(state with { World = state.World with { Scenes = scenes } }, [Effect.Message(MessageLevels.Success, "Watered!")]);
        }

        if (toolType == ToolTypes.Hoe && (tile.Background == TileTypes.Grass || tile.Background == TileTypes.Floor))
        {
            var scenes = new List<Scene>(state.World.Scenes);
            var sceneIndex = scenes.FindIndex(s => s.Id == scene.Id);
            if (sceneIndex == -1) return EngineStep.Of(state);
            var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
            foreach (var spot in AoeTargets(scene, x, y, state.Player.Direction, tier))
            {
                var spotTile = tiles[(int)spot.Y][(int)spot.X];
                if ((spotTile.Background == TileTypes.Grass || spotTile.Background == TileTypes.Floor) && spotTile.Node is null)
                {
                    tiles[(int)spot.Y][(int)spot.X] = spotTile with
                    {
                        Background = TileTypes.Soil,
                        Type = TileTypes.Soil,
                        SoilMoisture = 0,
                        SoilFertility = 0,
                        SoilState = SoilStates.Dry,
                    };
                }
            }
            scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };
            return Finish(state with { World = state.World with { Scenes = scenes } }, [Effect.Message(MessageLevels.Success, "Tilled soil!")]);
        }

        if (toolType == ToolTypes.Scythe && tile.Crop is not null)
        {
            if (tile.Crop.Withered == true)
            {
                var scenes = new List<Scene>(state.World.Scenes);
                var sceneIndex = scenes.FindIndex(s => s.Id == scene.Id);
                if (sceneIndex == -1) return EngineStep.Of(state);
                var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
                tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Crop = null };
                scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };
                return Finish(state with { World = state.World with { Scenes = scenes } }, [Effect.Message(MessageLevels.Success, "Cleared the withered crop.")]);
            }
            ctx.Content.Crops.TryGetValue(tile.Crop.Type, out var cropDef);
            if (cropDef is not null && Crops.IsCropMatureByDays(tile.Crop, cropDef))
            {
                var result = HarvestCrop(ctx, state, scene.Id, x, y);
                return Finish(result.State, result.Effects);
            }
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "Crop is not ready to harvest yet"));
        }

        if (toolType == ToolTypes.FishingRod && tile.Background == TileTypes.Water)
        {
            // A declared 'fishing' minigame gates the catch on player skill; the
            // score re-enters through the resolveMinigame command. Without one the
            // cast resolves instantly (original behavior).
            var minigame = ctx.Content.Minigames.Find(def => def.Id == ExtensibilitySchema.FishingMinigameId);
            if (minigame is not null && state.Minigame is null)
            {
                var context = new OrderedDictionary<string, JsonElement>
                {
                    ["builtin"] = Js.Value("fishing"),
                    ["rodTier"] = Js.Value(tier),
                };
                var opened = GameEvents.StartMinigameSession(ctx, state, minigame.Id, context);
                return Finish(opened.State, opened.Effects);
            }
            var result = Fishing.ResolveFishing(ctx, state, tier);
            return Finish(result.State, result.Effects);
        }

        return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"Can't use {toolSlot.Item.Name} here"));
    }

    public static EngineStep HandleInteract(EngineContext ctx, GameState state)
    {
        var target = FacingTile(state);

        var facing = WorldMovement.FacingTarget(state);
        var targetX = facing.X;
        var targetY = facing.Y;

        // Authored interact-events take priority over built-in interactions (M3)
        var eventResult = GameEvents.EvaluateEvents(ctx, state, "interact", new(targetX, targetY));
        // JS `eventResult.state !== state` is a reference comparison.
        if (!ReferenceEquals(eventResult.State, state) || eventResult.Effects.Count > 0)
        {
            return eventResult;
        }

        // NPC dialogue next (uses live NPC positions from state)
        string? npcEntryId = null;
        foreach (var (id, npc) in state.Npcs)
        {
            if (npc.SceneId == state.Player.SceneId && npc.X == targetX && npc.Y == targetY)
            {
                npcEntryId = id;
                break;
            }
        }
        if (npcEntryId is not null)
        {
            var npcDef = ctx.Content.Npcs.Find(npc => npc.Id == npcEntryId);
            if (npcDef is not null && npcDef.Dialogue is { Count: > 0 })
            {
                ctx.Hooks?.Emit(HookNames.OnNPCInteract, new NpcInteractHookPayload(npcDef.Id));
                var opened = state with { Dialogue = new DialogueState { NpcId = npcDef.Id, DialogueId = npcDef.Dialogue[0].Id } };
                var quests = Quests.ProgressQuests(ctx, opened, "talk", npcDef.Id, 1);
                return new EngineStep(quests.State, quests.Effects);
            }
            return EngineStep.Of(state);
        }

        if (target is null) return EngineStep.Of(state);
        var (scene, x, y, tile) = target;

        // Animals (M4c): feed → collect → pet
        var animal = Animals.AnimalAt(state, state.Player.SceneId, x, y);
        if (animal is not null)
        {
            return Animals.HandleAnimalInteraction(ctx, state, animal);
        }

        // Machines (M4a): collect finished output (loading goes through machineLoad)
        if (tile.Machine is not null)
        {
            if (tile.Machine.Output is { Count: > 0 })
            {
                return Crafting.CollectMachineOutput(ctx, state, scene.Id, x, y);
            }
            if (tile.Machine.Processing is not null)
            {
                return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "Still working…"));
            }
            var machineDef = ctx.Content.MachineTypes.Find(def => def.Id == tile.Machine.TypeId);
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, $"{machineDef?.Name ?? "Machine"} is idle — load a recipe."));
        }

        // Mine (M4f): entrance descends (elevator checkpoint when unlocked);
        // interacting near the entry tile of a floor climbs out.
        var mineConfig = ctx.Content.Mine;
        if (mineConfig.Enabled
            && state.Player.SceneId == (mineConfig.EntranceSceneId ?? "")
            && x == (mineConfig.EntranceX ?? -1) && y == (mineConfig.EntranceY ?? -1))
        {
            var checkpoint = Math.Floor(state.Mine.DeepestFloor / mineConfig.ElevatorEvery) * mineConfig.ElevatorEvery;
            return Mines.DescendMine(ctx, state, Math.Max(1, checkpoint));
        }
        if (Mines.IsMineScene(state.Player.SceneId) && x == 1 && y == 1)
        {
            return Mines.ExitMine(ctx, state);
        }

        if (tile.Crop is not null)
        {
            if (tile.Crop.Withered == true)
            {
                return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "This crop withered — clear it with a scythe."));
            }
            ctx.Content.Crops.TryGetValue(tile.Crop.Type, out var definition);
            if (definition is not null && !Crops.IsCropMatureByDays(tile.Crop, definition))
            {
                return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "Crop is not ready to harvest yet"));
            }
            return HarvestCrop(ctx, state, scene.Id, x, y);
        }

        if (tile.Background == TileTypes.Soil && !Gathering.IsNodeActive(tile))
        {
            return PlantSeed(ctx, state, scene.Id, x, y);
        }

        return EngineStep.Of(state);
    }

    private static EngineStep HarvestCrop(EngineContext ctx, GameState state, string sceneId, double x, double y)
    {
        var scene = WorldMovement.FindScene(state, sceneId);
        if (scene is null) return EngineStep.Of(state);
        var tile = scene.Tiles[(int)y][(int)x];
        var crop = tile.Crop;
        if (crop is null || crop.Withered == true) return EngineStep.Of(state);

        if (!ctx.Content.Crops.TryGetValue(crop.Type, out var definition)) return EngineStep.Of(state);
        if (!Crops.IsCropMatureByDays(crop, definition))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "Crop is not ready to harvest yet"));
        }

        var cropItem = ctx.Content.Items.Find(item => item.Id == $"crop-{crop.Type}");
        if (cropItem is null) return EngineStep.Of(state);

        var rng = new Rng(state.Rng);
        var mutation = Crops.RollMutation(definition, crop.Quality, rng);
        // Farming skill: +1 yield per 4 levels (M4g)
        var quantity = Crops.RollYield(definition, crop.Quality, mutation, rng) + Skills.FarmingYieldBonus(state);
        var estimatedValue = Crops.CalculateHarvestValue(definition, crop.Quality, mutation, quantity);

        var addResult = Inventory.AddItem(state.Player.Inventory, cropItem, quantity, state.Player.MaxInventorySize);
        if (!addResult.Added)
        {
            // Full inventory aborts the harvest; the rng draws are discarded.
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Inventory is full!"));
        }

        var scenes = new List<Scene>(state.World.Scenes);
        var sceneIndex = scenes.FindIndex(s => s.Id == sceneId);
        if (sceneIndex == -1) return EngineStep.Of(state);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        if (definition.CanRegrow)
        {
            var growthDays = Crops.CropGrowthDays(definition);
            var regrowth = Crops.CropRegrowthDays(definition);
            var regrown = crop with
            {
                DaysGrown = Math.Max(0, growthDays - regrowth),
                HarvestCount = crop.HarvestCount + 1,
                Watered = false,
            };
            tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Crop = regrown with { Stage = Crops.ComputeCropStage(regrown, definition) } };
        }
        else
        {
            tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Crop = null };
        }
        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        var qualityText = "";
        if (crop.Quality == CropQualities.Iridium) qualityText = " ⭐⭐⭐";
        else if (crop.Quality == CropQualities.Gold) qualityText = " ⭐⭐";
        else if (crop.Quality == CropQualities.Silver) qualityText = " ⭐";
        // JS toUpperCase on the ASCII mutation ids.
        var mutationText = !string.IsNullOrEmpty(mutation) ? $" ({mutation.ToUpperInvariant()}!)" : "";

        var nextState = state with
        {
            Rng = rng.State,
            World = state.World with { Scenes = scenes },
            Player = state.Player with { Inventory = addResult.Inventory },
        };

        var effects = new List<Effect>
        {
            Effect.Message(MessageLevels.Success, $"Harvested {Js.Num(quantity)}x {definition.Name}{qualityText}{mutationText} (worth ~${Js.Num(estimatedValue)})"),
            new CropHarvestedEffect(crop.Type, quantity),
        };
        ctx.Hooks?.Emit(HookNames.OnCropHarvest, new CropHarvestHookPayload(crop.Type, quantity, crop.Quality));

        var xpResult = Skills.GrantXp(ctx, nextState, "farming", 8);
        var questResult = Quests.ProgressQuests(ctx, xpResult.State, "harvest", crop.Type, quantity);
        return new EngineStep(questResult.State, [.. effects, .. xpResult.Effects, .. questResult.Effects]);
    }

    private static EngineStep PlantSeed(EngineContext ctx, GameState state, string sceneId, double x, double y)
    {
        var scene = WorldMovement.FindScene(state, sceneId);
        if (scene is null) return EngineStep.Of(state);

        var seedSlot = state.Player.Inventory.Find(slot => slot.Item.Type == ItemTypes.Seed);
        if (seedSlot is null || string.IsNullOrEmpty(seedSlot.Item.CropType))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Info, "No seeds in inventory"));
        }

        var cropType = seedSlot.Item.CropType;
        if (!ctx.Content.Crops.TryGetValue(cropType, out var definition))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Invalid crop type!"));
        }

        if (!Crops.CanGrowInSeason(definition, state.Clock.Season))
        {
            return EngineStep.Of(state, Effect.Message(MessageLevels.Error, $"{definition.Name} cannot grow in {state.Clock.Season}!"));
        }

        if (definition.MultiTile is not null)
        {
            var canPlace = Crops.CanPlaceMultiTileCrop(scene.Tiles, x, y, definition.MultiTile.Width, definition.MultiTile.Height);
            if (!canPlace)
            {
                return EngineStep.Of(state, Effect.Message(MessageLevels.Error, "Not enough space for this crop!"));
            }
        }

        var inventory = Inventory.RemoveItem(state.Player.Inventory, seedSlot.Item.Id, 1);
        var fertilizerSlot = inventory.Find(slot => slot.Item.Type == ItemTypes.Fertilizer);
        var usedFertilizer = fertilizerSlot is not null;
        if (fertilizerSlot is not null)
        {
            inventory = Inventory.RemoveItem(inventory, fertilizerSlot.Item.Id, 1);
        }

        var scenes = new List<Scene>(state.World.Scenes);
        var sceneIndex = scenes.FindIndex(s => s.Id == sceneId);
        if (sceneIndex == -1) return EngineStep.Of(state);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);

        var newCrop = Crops.CreatePlantedCrop(cropType, state.Clock.Day, usedFertilizer);

        if (definition.MultiTile is not null)
        {
            var multiTileId = $"{cropType}-{Js.Num(state.Clock.Tick)}-{Js.Num(x)}-{Js.Num(y)}";
            for (double cropDy = 0; cropDy < definition.MultiTile.Height; cropDy++)
            {
                for (double cropDx = 0; cropDx < definition.MultiTile.Width; cropDx++)
                {
                    var ty = (int)(y + cropDy);
                    var tx = (int)(x + cropDx);
                    tiles[ty][tx] = tiles[ty][tx] with
                    {
                        Crop = newCrop with
                        {
                            IsMultiTileRoot = cropDy == 0 && cropDx == 0,
                            MultiTileId = multiTileId,
                        },
                    };
                }
            }
        }
        else
        {
            tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Crop = newCrop };
        }

        if (usedFertilizer)
        {
            tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { SoilFertility = 100, SoilState = SoilStates.Fertilized };
        }

        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        return EngineStep.Of(
            state with
            {
                World = state.World with { Scenes = scenes },
                Player = state.Player with { Inventory = inventory },
            },
            Effect.Message(MessageLevels.Success, $"Planted {definition.Name}!{(usedFertilizer ? " (Fertilized)" : "")} Water it so it grows."));
    }
}
