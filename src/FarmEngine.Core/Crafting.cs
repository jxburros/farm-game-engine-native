using FarmEngine.Schemas;
using CraftableStatusRecord = FarmEngine.Core.CraftableStatus;

namespace FarmEngine.Core;

/// <summary>TS <c>CraftBlockReason</c> string union.</summary>
public static class CraftBlockReasons
{
    public const string Locked = "locked";
    public const string Ingredients = "ingredients";
    public const string Station = "station";
}

/// <summary>TS <c>CraftableStatus</c>. <c>Reason</c> is one of <see cref="CraftBlockReasons"/>.</summary>
public sealed record CraftableStatus(bool Craftable, string? Reason = null, string? Message = null);

/// <summary>
/// Crafting &amp; machines (M4a). Hand recipes craft instantly; machine recipes
/// load into a placed machine and complete after processingMinutes of game
/// time (absolute-minute comparison makes overnight catch-up automatic).
/// Port of crafting.ts.
/// </summary>
public static class Crafting
{
    /// <summary>Absolute game minute since day 1, 00:00.</summary>
    public static double AbsoluteMinute(GameState state) =>
        (state.Clock.Day - 1) * 24 * 60 + state.Clock.TimeMinutes;

    public static RecipeDefinition? RecipeById(EngineContext ctx, string recipeId) =>
        ctx.Content.Recipes.Find(recipe => recipe.Id == recipeId);

    public static bool IsRecipeUnlocked(EngineContext ctx, GameState state, RecipeDefinition recipe)
    {
        var unlock = recipe.Unlock;
        if (unlock == null) return true;
        if (unlock.Skill != null && Skills.SkillLevel(state, unlock.Skill.Skill) < unlock.Skill.Level) return false;
        if (!string.IsNullOrEmpty(unlock.QuestId) && !state.Player.CompletedQuests.Contains(unlock.QuestId)) return false;
        if (unlock.Seasons != null && unlock.Seasons.Count > 0 && !unlock.Seasons.Contains(state.Clock.Season)) return false;
        return true;
    }

    public static bool HasIngredients(GameState state, RecipeDefinition recipe) =>
        recipe.Inputs.All(input =>
        {
            double held = 0;
            foreach (var slot in state.Player.Inventory)
            {
                if (slot.Item.Id == input.ItemId) held = held + slot.Quantity;
            }
            return held >= input.Quantity;
        });

    /// <summary>TS <c>scene.tiles[y]?.[x]</c>.</summary>
    private static Tile? TileAt(Scene scene, double x, double y)
    {
        if (!(y >= 0 && y < scene.Tiles.Count) || y != Math.Floor(y)) return null;
        var row = scene.Tiles[(int)y];
        if (!(x >= 0 && x < row.Count) || x != Math.Floor(x)) return null;
        return row[(int)x];
    }

    /// <summary>
    /// Station categories provided by machines within a 1-tile radius (8-
    /// neighborhood, inclusive of the player's own tile) in the current scene.
    /// Player tile is floored — correct whether coordinates are integer grid
    /// cells or fractional (smooth-movement) positions.
    /// </summary>
    public static HashSet<string> NearbyStationCategories(EngineContext ctx, GameState state)
    {
        var categories = new HashSet<string>();
        var scene = WorldMovement.FindScene(state, state.Player.SceneId);
        if (scene == null) return categories;

        var px = Math.Floor(state.Player.X);
        var py = Math.Floor(state.Player.Y);
        for (var y = py - 1; y <= py + 1; y++)
        {
            for (var x = px - 1; x <= px + 1; x++)
            {
                var machine = TileAt(scene, x, y)?.Machine;
                if (machine == null) continue;
                var machineType = ctx.Content.MachineTypes.Find(type => type.Id == machine.TypeId);
                foreach (var category in machineType?.StationCategories ?? []) categories.Add(category);
            }
        }
        return categories;
    }

    /// <summary>The machine type (if any) that provides a given station category — used to name it in messages/UI.</summary>
    public static MachineTypeDefinition? StationProviding(EngineContext ctx, string category) =>
        ctx.Content.MachineTypes.Find(type => type.StationCategories.Contains(category));

    /// <summary>
    /// Why a hand-craftable recipe can't be crafted right now (locked / missing
    /// ingredients / missing station) — shared by the game-shell and editor
    /// crafting UIs so they don't duplicate the rules.
    /// </summary>
    public static CraftableStatusRecord CraftableStatus(EngineContext ctx, GameState state, RecipeDefinition recipe)
    {
        if (!IsRecipeUnlocked(ctx, state, recipe))
        {
            return new CraftableStatusRecord(false, CraftBlockReasons.Locked, "Recipe not unlocked yet.");
        }
        if (!HasIngredients(state, recipe))
        {
            return new CraftableStatusRecord(false, CraftBlockReasons.Ingredients, "Missing ingredients.");
        }
        if (!string.IsNullOrEmpty(recipe.RequiresStationCategory) && !NearbyStationCategories(ctx, state).Contains(recipe.RequiresStationCategory))
        {
            var station = StationProviding(ctx, recipe.RequiresStationCategory);
            var message = station != null
                ? $"You need to be near a {station.Name} to craft that."
                : $"You need to be near a {recipe.RequiresStationCategory} station to craft that.";
            return new CraftableStatusRecord(false, CraftBlockReasons.Station, message);
        }
        return new CraftableStatusRecord(true);
    }

    private static GameState ConsumeInputs(GameState state, RecipeDefinition recipe)
    {
        var inventory = state.Player.Inventory;
        foreach (var input in recipe.Inputs)
        {
            inventory = Inventory.RemoveItem(inventory, input.ItemId, input.Quantity);
        }
        return state with { Player = state.Player with { Inventory = inventory } };
    }

    private sealed record GrantOutputsResult(GameState State, List<Effect> Effects, bool AllAdded);

    private static GrantOutputsResult GrantOutputs(EngineContext ctx, GameState state, List<RecipeIngredient> outputs)
    {
        var inventory = state.Player.Inventory;
        var effects = new List<Effect>();
        var allAdded = true;
        foreach (var output in outputs)
        {
            var item = ctx.Content.Items.Find(i => i.Id == output.ItemId);
            if (item == null) continue;
            var result = Inventory.AddItem(inventory, item, output.Quantity, state.Player.MaxInventorySize);
            if (result.Added)
            {
                inventory = result.Inventory;
                effects.Add(Effect.Message("success", $"Crafted {FarmEngine.Json.Js.Num(output.Quantity)}x {item.Name}"));
            }
            else
            {
                allAdded = false;
                effects.Add(Effect.Message("error", "Inventory is full!"));
            }
        }
        return new GrantOutputsResult(state with { Player = state.Player with { Inventory = inventory } }, effects, allAdded);
    }

    /// <summary>Hand-craft an instant recipe.</summary>
    public static EngineStep HandleCraft(EngineContext ctx, GameState state, string recipeId)
    {
        var recipe = RecipeById(ctx, recipeId);
        if (recipe == null) return EngineStep.Of(state, Effect.Message("error", "Unknown recipe."));
        if (!string.IsNullOrEmpty(recipe.MachineTypeId))
        {
            return EngineStep.Of(state, Effect.Message("info", "That recipe needs a machine — load it there."));
        }
        var status = CraftableStatus(ctx, state, recipe);
        if (!status.Craftable)
        {
            return EngineStep.Of(state, Effect.Message("error", status.Message ?? "Cannot craft that right now."));
        }

        var current = ConsumeInputs(state, recipe);
        var granted = GrantOutputs(ctx, current, recipe.Outputs);
        current = granted.State;
        ctx.Hooks?.Emit(HookNames.OnRecipeCraft, new RecipeCraftHookPayload(recipeId));

        var quests = Quests.ProgressQuests(ctx, current, "craft", recipeId, 1);
        return new EngineStep(quests.State, [.. granted.Effects, .. quests.Effects]);
    }

    /// <summary>Place a machine (consumes its item) on the facing tile.</summary>
    public static EngineStep HandlePlaceMachine(EngineContext ctx, GameState state, string machineTypeId)
    {
        var machineType = ctx.Content.MachineTypes.Find(type => type.Id == machineTypeId);
        if (machineType == null) return EngineStep.Of(state, Effect.Message("error", "Unknown machine."));

        if (!string.IsNullOrEmpty(machineType.ItemId))
        {
            var held = state.Player.Inventory.Find(slot => slot.Item.Id == machineType.ItemId);
            if (held == null) return EngineStep.Of(state, Effect.Message("error", $"You need a {machineType.Name} in your inventory."));
        }

        var scene = WorldMovement.FindScene(state, state.Player.SceneId);
        if (scene == null) return EngineStep.Of(state);
        var (x, y) = WorldMovement.FacingTarget(state);
        if (x < 0 || x >= scene.Width || y < 0 || y >= scene.Height) return EngineStep.Of(state);
        var tile = scene.Tiles[(int)y][(int)x];
        if (tile.Collision || tile.Crop != null || tile.Node != null || tile.Machine != null || tile.Item != null)
        {
            return EngineStep.Of(state, Effect.Message("error", "No room to place it there."));
        }

        var scenes = new List<Scene>(state.World.Scenes);
        var sceneIndex = scenes.FindIndex(s => s.Id == scene.Id);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        tiles[(int)y][(int)x] = tiles[(int)y][(int)x] with { Machine = new TileMachine { TypeId = machineTypeId } };
        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        var inventory = !string.IsNullOrEmpty(machineType.ItemId)
            ? Inventory.RemoveItem(state.Player.Inventory, machineType.ItemId, 1)
            : state.Player.Inventory;

        return EngineStep.Of(
            state with { World = state.World with { Scenes = scenes }, Player = state.Player with { Inventory = inventory } },
            Effect.Message("success", $"Placed {machineType.Name}"));
    }

    /// <summary>Load a recipe into the machine on the facing tile.</summary>
    public static EngineStep HandleMachineLoad(EngineContext ctx, GameState state, string recipeId)
    {
        var scene = WorldMovement.FindScene(state, state.Player.SceneId);
        if (scene == null) return EngineStep.Of(state);
        var (x, y) = WorldMovement.FacingTarget(state);
        var tile = TileAt(scene, x, y);
        if (tile?.Machine == null) return EngineStep.Of(state, Effect.Message("info", "No machine there."));
        if (tile.Machine.Processing != null) return EngineStep.Of(state, Effect.Message("info", "It is already working."));
        if (tile.Machine.Output != null && tile.Machine.Output.Count > 0)
        {
            return EngineStep.Of(state, Effect.Message("info", "Collect the finished goods first."));
        }

        var recipe = RecipeById(ctx, recipeId);
        if (recipe == null) return EngineStep.Of(state, Effect.Message("error", "Unknown recipe."));
        if (recipe.MachineTypeId != tile.Machine.TypeId)
        {
            return EngineStep.Of(state, Effect.Message("error", "This machine cannot run that recipe."));
        }
        if (!IsRecipeUnlocked(ctx, state, recipe))
        {
            return EngineStep.Of(state, Effect.Message("error", "Recipe not unlocked yet."));
        }
        if (!HasIngredients(state, recipe))
        {
            return EngineStep.Of(state, Effect.Message("error", "Missing ingredients."));
        }

        var current = ConsumeInputs(state, recipe);
        var scenes = new List<Scene>(current.World.Scenes);
        var sceneIndex = scenes.FindIndex(s => s.Id == scene.Id);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        var target = tiles[(int)y][(int)x];
        tiles[(int)y][(int)x] = target with
        {
            Machine = target.Machine! with
            {
                Processing = new MachineProcessing { RecipeId = recipeId, CompletesAtMinute = AbsoluteMinute(current) + recipe.ProcessingMinutes },
            },
        };
        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        return EngineStep.Of(
            current with { World = current.World with { Scenes = scenes } },
            Effect.Message("success", $"Started {recipe.Name}"));
    }

    /// <summary>
    /// Settle machine jobs whose completion time has passed (called from ticks
    /// and the nightly pass — overnight processing "catches up" for free since
    /// completion is an absolute-minute comparison).
    /// </summary>
    public static GameState SettleMachines(EngineContext ctx, GameState state)
    {
        var now = AbsoluteMinute(state);
        var changed = false;

        var scenes = state.World.Scenes.Select(scene =>
        {
            var sceneChanged = false;
            var tiles = scene.Tiles.Select(row => row.Select(tile =>
            {
                if (tile.Machine?.Processing == null || tile.Machine.Processing.CompletesAtMinute > now) return tile;
                var recipe = RecipeById(ctx, tile.Machine.Processing.RecipeId);
                sceneChanged = true;
                return tile with
                {
                    Machine = tile.Machine with
                    {
                        Processing = null,
                        Output = recipe != null ? recipe.Outputs : [],
                    },
                };
            }).ToList()).ToList();
            if (!sceneChanged) return scene;
            changed = true;
            return scene with { Tiles = tiles };
        }).ToList();

        if (!changed) return state;
        return state with { World = state.World with { Scenes = scenes } };
    }

    /// <summary>Collect finished machine output (invoked from interact).</summary>
    public static EngineStep CollectMachineOutput(EngineContext ctx, GameState state, string sceneId, double x, double y)
    {
        var scene = WorldMovement.FindScene(state, sceneId);
        var tile = scene == null ? null : TileAt(scene, x, y);
        if (scene == null || tile?.Machine?.Output == null || tile.Machine.Output.Count == 0) return EngineStep.Of(state);

        var granted = GrantOutputs(ctx, state, tile.Machine.Output);
        if (!granted.AllAdded)
        {
            return new EngineStep(state, granted.Effects);
        }

        var scenes = new List<Scene>(granted.State.World.Scenes);
        var sceneIndex = scenes.FindIndex(s => s.Id == sceneId);
        var tiles = Tiles.CloneTiles(scenes[sceneIndex].Tiles);
        var target = tiles[(int)y][(int)x];
        tiles[(int)y][(int)x] = target with { Machine = target.Machine! with { Output = null } };
        scenes[sceneIndex] = scenes[sceneIndex] with { Tiles = tiles };

        return new EngineStep(granted.State with { World = granted.State.World with { Scenes = scenes } }, granted.Effects);
    }
}
