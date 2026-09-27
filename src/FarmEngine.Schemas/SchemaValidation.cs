using FarmEngine.Json;

namespace FarmEngine.Schemas;

/// <summary>
/// The C# records don't enforce zod refinements (int, positive, min/max,
/// enum membership, …). <see cref="ValidateProject"/> re-checks exactly the
/// constraints the web's zod schemas enforce on parse, so a project the web
/// version accepts loads here too, and reports them as <c>path: message</c>
/// strings using zod's dotted issue paths (e.g. <c>scenes.0.tiles.1.2.background</c>).
/// <see cref="LintProject"/> adds the structural checks zod does not have
/// (empty ids, duplicate scene ids, tile grid vs. width/height, a dangling
/// start scene, inverted regions and ranges): they are problems for the
/// Problems panel, never reasons to refuse a file. Not exhaustive.
/// </summary>
public static class SchemaValidation
{
    private static readonly IReadOnlyList<string> NpcBirthdaySeasons = PrimitivesSchema.ClassicSeasons;

    /// <summary>Parse-level checks only (what zod rejects). Import and migrations use this.</summary>
    public static IReadOnlyList<string> ValidateProject(GameProject p) => Collect(p, parse: true, lint: false);

    /// <summary>
    /// Structural lint zod cannot express. The web editor lets creators save all of these, so
    /// they are reported, not rejected.
    /// </summary>
    public static IReadOnlyList<string> LintProject(GameProject p) => Collect(p, parse: false, lint: true);

    private static IReadOnlyList<string> Collect(GameProject p, bool parse, bool lint)
    {
        var errors = new List<string>();
        var parseErrors = new List<string>();
        var lintErrors = new List<string>();
        void Error(string path, string message) => parseErrors.Add($"{path}: {message}");
        void Lint(string path, string message) => lintErrors.Add($"{path}: {message}");

        void Int(string path, double value)
        {
            if (!Js.IsInteger(value)) Error(path, $"Expected integer, received {Js.Num(value)}");
        }
        void PositiveInt(string path, double value)
        {
            if (!Js.IsInteger(value)) Error(path, $"Expected integer, received {Js.Num(value)}");
            else if (value <= 0) Error(path, "Number must be greater than 0");
        }
        void Positive(string path, double value)
        {
            if (!(value > 0)) Error(path, "Number must be greater than 0");
        }
        void NonNegative(string path, double value)
        {
            if (!(value >= 0)) Error(path, "Number must be greater than or equal to 0");
        }
        void Range(string path, double value, double min, double max)
        {
            if (!(value >= min && value <= max)) Error(path, $"Number must be between {Js.Num(min)} and {Js.Num(max)}");
        }
        // zod: `z.string()` accepts "" — an empty id is a lint problem, not a parse error.
        void NonEmpty(string path, string? value)
        {
            if (string.IsNullOrEmpty(value)) Lint(path, "Required id must be a non-empty string");
        }
        void OneOf(string path, string? value, IReadOnlyList<string> allowed, bool optional = false)
        {
            if (value is null)
            {
                if (!optional) Error(path, "Required");
                return;
            }
            if (!allowed.Contains(value))
            {
                Error(path, $"Invalid enum value. Expected {string.Join(" | ", allowed.Select(a => $"'{a}'"))}, received '{value}'");
            }
        }

        Int("schemaVersion", p.SchemaVersion);
        NonEmpty("id", p.Id);
        OneOf("mode", p.Mode, EditorModes.All);
        OneOf("selectedTileType", p.SelectedTileType, TileTypes.All);
        Int("currentYear", p.CurrentYear);

        // Scenes
        var sceneIds = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < p.Scenes.Count; s++)
        {
            var scene = p.Scenes[s];
            var sp = $"scenes.{s}";
            NonEmpty($"{sp}.id", scene.Id);
            if (!string.IsNullOrEmpty(scene.Id) && !sceneIds.Add(scene.Id)) Lint($"{sp}.id", $"Duplicate scene id '{scene.Id}'");
            PositiveInt($"{sp}.width", scene.Width);
            PositiveInt($"{sp}.height", scene.Height);

            if (Js.IsInteger(scene.Height) && scene.Height > 0 && scene.Tiles.Count != scene.Height)
            {
                Lint($"{sp}.tiles", $"Expected {Js.Num(scene.Height)} rows (scene height), found {scene.Tiles.Count}");
            }
            for (var y = 0; y < scene.Tiles.Count; y++)
            {
                var row = scene.Tiles[y];
                if (row is null)
                {
                    Error($"{sp}.tiles.{y}", "Expected array, received null");
                    continue;
                }
                if (Js.IsInteger(scene.Width) && scene.Width > 0 && row.Count != scene.Width)
                {
                    Lint($"{sp}.tiles.{y}", $"Expected {Js.Num(scene.Width)} tiles (scene width), found {row.Count}");
                }
                for (var x = 0; x < row.Count; x++)
                {
                    var tile = row[x];
                    var tp = $"{sp}.tiles.{y}.{x}";
                    if (tile is null)
                    {
                        Error(tp, "Expected object, received null");
                        continue;
                    }
                    OneOf($"{tp}.type", tile.Type, TileTypes.All);
                    OneOf($"{tp}.background", tile.Background, TileTypes.All);
                    OneOf($"{tp}.overlay", tile.Overlay, TileTypes.All, optional: true);
                    OneOf($"{tp}.object", tile.Object, TileTypes.All, optional: true);
                    OneOf($"{tp}.soilState", tile.SoilState, SoilStates.All, optional: true);
                    if (tile.Crop is { } crop)
                    {
                        OneOf($"{tp}.crop.quality", crop.Quality, CropQualities.All);
                        OneOf($"{tp}.crop.mutation", crop.Mutation, CropMutations.All, optional: true);
                    }
                }
            }
            for (var t = 0; t < scene.Transitions.Count; t++)
            {
                NonEmpty($"{sp}.transitions.{t}.toSceneId", scene.Transitions[t].ToSceneId);
            }
        }
        if (p.Scenes.Count > 0 && !sceneIds.Contains(p.StartSceneId))
        {
            Lint("startSceneId", $"No scene with id '{p.StartSceneId}'");
        }

        // Player
        OneOf("player.direction", p.Player.Direction, Directions.All);
        NonEmpty("player.sceneId", p.Player.SceneId);
        for (var i = 0; i < p.Player.Inventory.Count; i++)
        {
            NonEmpty($"player.inventory.{i}.item.id", p.Player.Inventory[i].Item?.Id);
        }

        // Items
        for (var i = 0; i < p.Items.Count; i++)
        {
            var item = p.Items[i];
            var ip = $"items.{i}";
            NonEmpty($"{ip}.id", item.Id);
            OneOf($"{ip}.type", item.Type, ItemTypes.All);
            OneOf($"{ip}.toolType", item.ToolType, ToolTypes.All, optional: true);
            if (item.ToolTier is { } tier) PositiveInt($"{ip}.toolTier", tier);
        }

        // NPCs
        for (var i = 0; i < p.Npcs.Count; i++)
        {
            var npc = p.Npcs[i];
            var np = $"npcs.{i}";
            NonEmpty($"{np}.id", npc.Id);
            OneOf($"{np}.movePattern", npc.MovePattern, NpcMovePatterns.All, optional: true);
            if (npc.WanderRadius is { } radius) PositiveInt($"{np}.wanderRadius", radius);
            if (npc.PatrolPoints is { } points)
            {
                for (var k = 0; k < points.Count; k++)
                {
                    Int($"{np}.patrolPoints.{k}.x", points[k].X);
                    Int($"{np}.patrolPoints.{k}.y", points[k].Y);
                }
            }
            if (npc.Birthday is { } birthday)
            {
                OneOf($"{np}.birthday.season", birthday.Season, NpcBirthdaySeasons);
                Int($"{np}.birthday.day", birthday.Day);
            }
            for (var d = 0; d < npc.Dialogue.Count; d++) NonEmpty($"{np}.dialogue.{d}.id", npc.Dialogue[d].Id);
        }

        for (var d = 0; d < p.Dialogues.Count; d++) NonEmpty($"dialogues.{d}.id", p.Dialogues[d].Id);

        // Quests
        for (var q = 0; q < p.Quests.Count; q++)
        {
            var quest = p.Quests[q];
            var qp = $"quests.{q}";
            NonEmpty($"{qp}.id", quest.Id);
            OneOf($"{qp}.status", quest.Status, QuestStatuses.All);
            for (var o = 0; o < quest.Objectives.Count; o++)
            {
                NonEmpty($"{qp}.objectives.{o}.id", quest.Objectives[o].Id);
                OneOf($"{qp}.objectives.{o}.type", quest.Objectives[o].Type, QuestObjectiveTypes.All);
            }
        }

        // Events
        for (var e = 0; e < p.Events.Count; e++)
        {
            var ev = p.Events[e];
            var ep = $"events.{e}";
            NonEmpty($"{ep}.id", ev.Id);
            OneOf($"{ep}.trigger", ev.Trigger, EventTriggers.All);
            ValidateConditions(ep, ev.Conditions, Error, Lint, PositiveInt, OneOf);
            ValidateOutcomes(ep, ev.Outcomes, Error, Int, Range, OneOf);
        }

        // Actions & minigames
        for (var a = 0; a < p.Actions.Count; a++)
        {
            var action = p.Actions[a];
            var ap = $"actions.{a}";
            NonEmpty($"{ap}.id", action.Id);
            NonNegative($"{ap}.energyCost", action.EnergyCost);
            if (action.Hotkey is { Length: > 1 }) Error($"{ap}.hotkey", "String must contain at most 1 character(s)");
            ValidateConditions(ap, action.Conditions, Error, Lint, PositiveInt, OneOf);
            ValidateOutcomes(ap, action.Outcomes, Error, Int, Range, OneOf);
        }
        for (var m = 0; m < p.Minigames.Count; m++)
        {
            var minigame = p.Minigames[m];
            NonEmpty($"minigames.{m}.id", minigame.Id);
            for (var t = 0; t < minigame.ResultTiers.Count; t++)
            {
                Range($"minigames.{m}.resultTiers.{t}.minScore", minigame.ResultTiers[t].MinScore, 0, 1);
                ValidateOutcomes($"minigames.{m}.resultTiers.{t}", minigame.ResultTiers[t].Outcomes, Error, Int, Range, OneOf);
            }
        }

        // Content definitions
        foreach (var (list, name) in new[] { (p.Shops.Select(x => x.Id), "shops"), (p.Recipes.Select(x => x.Id), "recipes"),
                     (p.MachineTypes.Select(x => x.Id), "machineTypes"), (p.AnimalSpecies.Select(x => x.Id), "animalSpecies"),
                     (p.FishTables.Select(x => x.Id), "fishTables"), (p.Animals.Select(x => x.Id), "animals") })
        {
            var index = 0;
            foreach (var id in list) NonEmpty($"{name}.{index++}.id", id);
        }
        for (var n = 0; n < p.NodeTypes.Count; n++)
        {
            var node = p.NodeTypes[n];
            NonEmpty($"nodeTypes.{n}.id", node.Id);
            PositiveInt($"nodeTypes.{n}.health", node.Health);
            OneOf($"nodeTypes.{n}.requiredTool", node.RequiredTool, ToolTypes.All);
            PositiveInt($"nodeTypes.{n}.requiredToolTier", node.RequiredToolTier);
            if (node.RespawnDays is { } respawn) PositiveInt($"nodeTypes.{n}.respawnDays", respawn);
        }
        for (var r = 0; r < p.Recipes.Count; r++)
        {
            NonNegative($"recipes.{r}.processingMinutes", p.Recipes[r].ProcessingMinutes);
            for (var k = 0; k < p.Recipes[r].Inputs.Count; k++) PositiveInt($"recipes.{r}.inputs.{k}.quantity", p.Recipes[r].Inputs[k].Quantity);
            for (var k = 0; k < p.Recipes[r].Outputs.Count; k++) PositiveInt($"recipes.{r}.outputs.{k}.quantity", p.Recipes[r].Outputs[k].Quantity);
        }
        for (var w = 0; w < p.Weather.Types.Count; w++)
        {
            NonEmpty($"weather.types.{w}.id", p.Weather.Types[w].Id);
            Range($"weather.types.{w}.cropDamageChance", p.Weather.Types[w].CropDamageChance, 0, 1);
        }
        foreach (var (season, entries) in p.Weather.Table)
        {
            for (var k = 0; k < entries.Count; k++) Positive($"weather.table.{season}.{k}.weight", entries[k].Weight);
        }

        // Settings
        var settings = p.Settings;
        Positive("settings.maxEnergy", settings.MaxEnergy);
        Range("settings.collapseEnergyFraction", settings.CollapseEnergyFraction, 0, 1);
        NonNegative("settings.collapseMoneyPenalty", settings.CollapseMoneyPenalty);
        Positive("settings.movement.playerSpeed", settings.Movement.PlayerSpeed);
        Int("settings.time.dayStartMinute", settings.Time.DayStartMinute);
        Int("settings.time.dayEndMinute", settings.Time.DayEndMinute);
        Positive("settings.time.minutesPerRealSecond", settings.Time.MinutesPerRealSecond);
        for (var c = 0; c < settings.Calendar.Seasons.Count; c++)
        {
            NonEmpty($"settings.calendar.seasons.{c}.id", settings.Calendar.Seasons[c].Id);
            PositiveInt($"settings.calendar.seasons.{c}.days", settings.Calendar.Seasons[c].Days);
        }
        for (var f = 0; f < settings.Calendar.Festivals.Count; f++)
        {
            NonEmpty($"settings.calendar.festivals.{f}.id", settings.Calendar.Festivals[f].Id);
            PositiveInt($"settings.calendar.festivals.{f}.day", settings.Calendar.Festivals[f].Day);
        }

        // Mine
        var mine = p.Mine;
        PositiveInt("mine.floors", mine.Floors);
        PositiveInt("mine.floorWidth", mine.FloorWidth);
        PositiveInt("mine.floorHeight", mine.FloorHeight);
        PositiveInt("mine.elevatorEvery", mine.ElevatorEvery);
        Range("mine.ladderChance", mine.LadderChance, 0, 1);
        for (var b = 0; b < mine.Bands.Count; b++)
        {
            PositiveInt($"mine.bands.{b}.fromFloor", mine.Bands[b].FromFloor);
            PositiveInt($"mine.bands.{b}.toFloor", mine.Bands[b].ToFloor);
            Range($"mine.bands.{b}.density", mine.Bands[b].Density, 0, 1);
        }
        if (p.MineDeepestFloor is { } deepest)
        {
            Int("mineDeepestFloor", deepest);
            NonNegative("mineDeepestFloor", deepest);
        }

        // Content packs
        for (var i = 0; i < p.ContentPacks.Count; i++)
        {
            var id = p.ContentPacks[i].Pack.Manifest.Id;
            if (!PacksSchema.IsValidPackId(id))
            {
                Error($"contentPacks.{i}.pack.manifest.id", "pack ids must be lowercase letters, digits and dashes");
            }
        }

        if (p.RngState is { } rng)
        {
            if (rng.Algorithm != "xoshiro128ss") Error("rngState.algorithm", "Invalid literal value, expected \"xoshiro128ss\"");
            if (rng.S is null || rng.S.Length != 4) Error("rngState.s", "Expected a tuple of 4 integers");
        }

        if (parse) errors.AddRange(parseErrors);
        if (lint) errors.AddRange(lintErrors);
        return errors;
    }

    /// <summary>
    /// <see cref="ValidateProject"/> for an <see cref="ExportedGame"/>: the same
    /// checks minus the editor-only fields (<c>id</c>, <c>mode</c>,
    /// <c>selectedTileType</c>) and the player when the export carries none.
    /// </summary>
    public static IReadOnlyList<string> ValidateExportedGame(ExportedGame game)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(game, JsonDefaults.Options)!.AsObject();
        node["id"] = "exported-game";
        node["mode"] = EditorModes.Play;
        node["selectedTileType"] = TileTypes.Grass;
        if (game.Player is null) node["player"] = new System.Text.Json.Nodes.JsonObject { ["direction"] = Directions.Down, ["sceneId"] = "exported-game" };
        var project = System.Text.Json.JsonSerializer.Deserialize<GameProject>(node, JsonDefaults.Options)!;
        string[] skipped = game.Player is null ? ["id:", "mode:", "selectedTileType:", "player."] : ["id:", "mode:", "selectedTileType:"];
        return ValidateProject(project)
            .Where(e => !skipped.Any(prefix => e.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Refinement checks for a loaded <see cref="GameState"/> (zod
    /// <c>GameStateSchema</c>): integer fields, enums, the move-intent range and
    /// the RNG tuple. Not exhaustive.
    /// </summary>
    public static IReadOnlyList<string> ValidateGameState(GameState s)
    {
        var errors = new List<string>();
        void Error(string path, string message) => errors.Add($"{path}: {message}");
        void Int(string path, double value)
        {
            if (!Js.IsInteger(value)) Error(path, $"Expected integer, received {Js.Num(value)}");
        }
        void OneOf(string path, string? value, IReadOnlyList<string> allowed)
        {
            if (value is null) Error(path, "Required");
            else if (!allowed.Contains(value))
                Error(path, $"Invalid enum value. Expected {string.Join(" | ", allowed.Select(a => $"'{a}'"))}, received '{value}'");
        }

        Int("meta.saveVersion", s.Meta.SaveVersion);
        Int("clock.tick", s.Clock.Tick);
        Int("clock.day", s.Clock.Day);
        Int("clock.year", s.Clock.Year);

        var player = s.Player;
        OneOf("player.direction", player.Direction, Directions.All);
        foreach (var (axis, value) in new[] { ("dx", player.MoveIntent.Dx), ("dy", player.MoveIntent.Dy) })
        {
            Int($"player.moveIntent.{axis}", value);
            if (value < -1) Error($"player.moveIntent.{axis}", "Number must be greater than or equal to -1");
            if (value > 1) Error($"player.moveIntent.{axis}", "Number must be less than or equal to 1");
        }
        foreach (var (name, skill) in player.Skills) Int($"player.skills.{name}.level", skill.Level);

        foreach (var (id, npc) in s.Npcs)
        {
            Int($"npcs.{id}.x", npc.X);
            Int($"npcs.{id}.y", npc.Y);
            if (npc.PatrolIndex is { } patrol) Int($"npcs.{id}.patrolIndex", patrol);
            if (npc.Path is { } path)
            {
                for (var i = 0; i < path.Count; i++)
                {
                    Int($"npcs.{id}.path.{i}.x", path[i].X);
                    Int($"npcs.{id}.path.{i}.y", path[i].Y);
                }
            }
        }
        foreach (var (id, quest) in s.Quests) OneOf($"quests.{id}.status", quest.Status, QuestStatuses.All);

        Int("mine.deepestFloor", s.Mine.DeepestFloor);
        Int("mine.currentFloor", s.Mine.CurrentFloor);

        if (s.Rng.Algorithm != "xoshiro128ss") Error("rng.algorithm", "Invalid literal value, expected \"xoshiro128ss\"");
        if (s.Rng.S is null || s.Rng.S.Length != 4) Error("rng.s", "Expected a tuple of 4 integers");

        return errors;
    }

    private static void ValidateConditions(
        string basePath,
        List<EventCondition> conditions,
        Action<string, string> error,
        Action<string, string> lint,
        Action<string, double> positiveInt,
        Action<string, string?, IReadOnlyList<string>, bool> oneOf)
    {
        for (var c = 0; c < conditions.Count; c++)
        {
            var cp = $"{basePath}.conditions.{c}";
            switch (conditions[c])
            {
                case null:
                    error(cp, "Expected object, received null");
                    break;
                case EnterTileCondition t:
                    RegionSanity(cp, t.X, t.Y, t.X2, t.Y2, lint);
                    break;
                case InteractTileCondition t:
                    RegionSanity(cp, t.X, t.Y, t.X2, t.Y2, lint);
                    break;
                case HasItemCondition h:
                    if (string.IsNullOrEmpty(h.ItemId)) lint($"{cp}.itemId", "Required id must be a non-empty string");
                    break;
                case InventorySpaceCondition s:
                    if (string.IsNullOrEmpty(s.ItemId)) lint($"{cp}.itemId", "Required id must be a non-empty string");
                    positiveInt($"{cp}.quantity", s.Quantity);
                    break;
                case FlagCondition f:
                    if (string.IsNullOrEmpty(f.Flag)) lint($"{cp}.flag", "Flag name must be a non-empty string");
                    break;
                case DayRangeCondition d:
                    if (d.MinDay is { } minDay && d.MaxDay is { } maxDay && minDay > maxDay) lint(cp, "minDay is greater than maxDay");
                    break;
                case YearRangeCondition y:
                    if (y.MinYear is { } minYear && y.MaxYear is { } maxYear && minYear > maxYear) lint(cp, "minYear is greater than maxYear");
                    break;
                case TimeOfDayCondition t:
                    if (t.MinMinute > t.MaxMinute) lint(cp, "minMinute is greater than maxMinute");
                    break;
                case QuestStatusCondition q:
                    if (string.IsNullOrEmpty(q.QuestId)) lint($"{cp}.questId", "Required id must be a non-empty string");
                    oneOf($"{cp}.status", q.Status, QuestStatuses.All, false);
                    break;
                case FriendshipCondition f:
                    if (string.IsNullOrEmpty(f.NpcId)) lint($"{cp}.npcId", "Required id must be a non-empty string");
                    break;
                case FestivalIdCondition f:
                    if (string.IsNullOrEmpty(f.FestivalId)) lint($"{cp}.festivalId", "Required id must be a non-empty string");
                    break;
            }
        }
    }

    private static void RegionSanity(string path, double x, double y, double? x2, double? y2, Action<string, string> error)
    {
        if (x2 is { } ex && ex < x) error($"{path}.x2", "x2 must be >= x");
        if (y2 is { } ey && ey < y) error($"{path}.y2", "y2 must be >= y");
    }

    private static void ValidateOutcomes(
        string basePath,
        List<EventOutcome> outcomes,
        Action<string, string> error,
        Action<string, double> integer,
        Action<string, double, double, double> range,
        Action<string, string?, IReadOnlyList<string>, bool> oneOf)
    {
        for (var o = 0; o < outcomes.Count; o++)
        {
            var outcome = outcomes[o];
            var op = $"{basePath}.outcomes.{o}";
            if (outcome is null)
            {
                error(op, "Expected object, received null");
                continue;
            }
            oneOf($"{op}.type", outcome.Type, EventOutcomeTypes.All, false);
            if (outcome.Amount is { } amount && !double.IsFinite(amount)) error($"{op}.amount", "Number must be finite");
            if (outcome.Radius is { } radius)
            {
                integer($"{op}.radius", radius);
                range($"{op}.radius", radius, 0, 10);
            }
            oneOf($"{op}.newTileType", outcome.NewTileType, TileTypes.All, true);
        }
    }
}
