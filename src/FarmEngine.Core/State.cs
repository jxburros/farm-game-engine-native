using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Project → content/state bridge (port of state.ts). <see cref="EngineContext"/>
/// itself lives in EngineTypes.cs.
/// </summary>
public static class EngineState
{
    /// <summary>
    /// Content derived from the project's own fields (built-in fallbacks +
    /// authored content) — before packs layer on top.
    /// </summary>
    public static GameContent CreateBaseContentFromProject(GameProject project)
    {
        var settings = ResolveSettings(project.Settings);
        var nodeTypeIds = new HashSet<string>((project.NodeTypes ?? []).Select(def => def.Id));
        var weather = project.Weather ?? MigrationsSchema.DefaultWeatherConfig();
        return new GameContent
        {
            ContentVersion = GameContentSchema.CurrentContentVersion,
            Crops = Crops.MergeCropDefinitions(project.CustomCrops?.Select(ToCropDefinition).ToList()),
            Items = project.Items is { Count: > 0 } ? project.Items : ContentBuiltin.CreateDefaultItems(),
            Npcs = project.Npcs ?? [],
            Dialogues = project.Dialogues ?? [],
            Quests = project.Quests ?? [],
            Events = project.Events ?? [],
            Shops = project.Shops ?? [],
            // Built-in node types are always available; project definitions override by id.
            NodeTypes =
            [
                .. ContentBuiltin.DefaultNodeTypes.Where(def => !nodeTypeIds.Contains(def.Id)),
                .. ContentBuiltin.MineNodeTypes.Where(def => !nodeTypeIds.Contains(def.Id)),
                .. project.NodeTypes ?? [],
            ],
            Settings = settings,
            Recipes = project.Recipes ?? [],
            MachineTypes = project.MachineTypes ?? [],
            // WeatherConfigSchema.safeParse(...) ? parse(...) : parse(defaultWeatherConfig())
            Weather = IsValidWeatherConfig(weather) ? weather : MigrationsSchema.DefaultWeatherConfig(),
            AnimalSpecies = project.AnimalSpecies ?? [],
            FishTables = project.FishTables ?? [],
            // MineConfigSchema.parse(project.mine ?? { enabled: false }); `new()` carries the zod defaults.
            Mine = project.Mine ?? new MineConfig { Enabled = false },
            Actions = project.Actions ?? [],
            Minigames = project.Minigames ?? [],
            Scenes = project.Scenes ?? [],
            StartSceneId = project.StartSceneId,
        };
    }

    /// <summary>
    /// Derive the immutable content view from an editor project: base content,
    /// then enabled content packs layered on top (M5) with explicit override
    /// semantics.
    /// </summary>
    public static GameContent CreateContentFromProject(GameProject project)
    {
        var merged = Packs.MergePacksIntoContent(CreateBaseContentFromProject(project), project.ContentPacks ?? []).Content;
        // Localized game text from pack string tables (M7); authored text is the fallback.
        return Packs.ApplyLocaleStrings(merged, project.ContentPacks ?? [], merged.Settings.Locale ?? "en");
    }

    /// <summary>
    /// Create a running GameState from a project. The world starts as a deep
    /// copy of the project's scenes (which carry any in-progress crops from
    /// previous play sessions — historical behavior preserved until the M3
    /// playtest sandbox separates them).
    /// </summary>
    /// <param name="seed">TS <c>options.seed</c>.</param>
    public static GameState CreateGameState(GameProject project, string? seed = null)
    {
        var quests = new OrderedDictionary<string, QuestProgress>();
        foreach (var quest in project.Quests ?? [])
        {
            var objectives = new OrderedDictionary<string, QuestObjectiveProgress>();
            foreach (var objective in quest.Objectives)
            {
                objectives[objective.Id] = new QuestObjectiveProgress { Progress = objective.Progress, Completed = objective.Completed };
            }
            quests[quest.Id] = new QuestProgress { Status = quest.Status, Objectives = objectives };
        }

        var npcs = new OrderedDictionary<string, NpcState>();
        foreach (var npc in project.Npcs ?? [])
        {
            npcs[npc.Id] = new NpcState { X = npc.X, Y = npc.Y, SceneId = npc.SceneId };
        }

        var resolvedSettings = ResolveSettings(project.Settings);
        var maxEnergy = project.Player.MaxEnergy ?? resolvedSettings.MaxEnergy;
        var engineSeed = seed ?? $"{project.Id}:{Js.Num(project.GameStartTime)}";

        var flags = new OrderedDictionary<string, JsonElement>();
        foreach (var (key, value) in project.EventFlags ?? []) flags[key] = Js.Value(value);

        var state = new GameState
        {
            Meta = new GameStateMeta
            {
                SaveVersion = SaveSchema.CurrentSaveVersion,
                EngineSeed = engineSeed,
                Packs = Packs.StampPacks(project.ContentPacks ?? []),
            },
            Clock = new ClockState
            {
                Tick = 0,
                // TS `project.currentTimeMinutes ?? dayStartMinute` / `currentYear ?? 1`: both
                // are required (non-nullable) project fields in C#, so the fallbacks never apply.
                TimeMinutes = project.CurrentTimeMinutes,
                Day = project.CurrentDay,
                Season = project.CurrentSeason,
                Year = project.CurrentYear,
                WeatherId = project.CurrentWeatherId ?? "sun",
            },
            World = new WorldState
            {
                Scenes = JsonDefaults.DeepClone(project.Scenes),
            },
            Player = new PlayerState
            {
                // Projects may store tile indices (legacy/authored) or fractional
                // free-movement positions; tile indices land on the tile center.
                X = SaveSchema.CenterCoordinate(project.Player.X),
                Y = SaveSchema.CenterCoordinate(project.Player.Y),
                MoveIntent = new MoveIntent { Dx = 0, Dy = 0 },
                Direction = project.Player.Direction,
                SceneId = project.Player.SceneId,
                Inventory = JsonDefaults.DeepClone(project.Player.Inventory),
                MaxInventorySize = project.Player.MaxInventorySize,
                Money = project.Player.Money,
                Energy = project.Player.Energy ?? maxEnergy,
                MaxEnergy = maxEnergy,
                Skills = JsonDefaults.DeepClone(project.Player.Skills ?? []),
                ActiveQuests = [.. project.Player.ActiveQuests],
                CompletedQuests = [.. project.Player.CompletedQuests],
                EquippedTool = project.Player.EquippedTool,
            },
            Npcs = npcs,
            Quests = quests,
            Dialogue = null,
            Shop = null,
            Minigame = null,
            ShopPurchasesToday = [],
            Social = JsonDefaults.DeepClone(project.SocialState ?? []),
            Animals = JsonDefaults.DeepClone(project.Animals ?? []),
            Mine = new MineProgress { DeepestFloor = project.MineDeepestFloor ?? 0, CurrentFloor = 0 },
            Flags = flags,
            QuarantinedItems = JsonDefaults.DeepClone(project.QuarantinedItems ?? []),
            Rng = project.RngState ?? RngMath.CreateRngState(engineSeed),
        };

        // Items from missing/disabled packs are quarantined, not dropped; they
        // come back when the pack does.
        var enabledPacks = new HashSet<string>(
            (project.ContentPacks ?? []).Where(install => install.Enabled).Select(install => install.Pack.Manifest.Id));
        return Packs.ReconcilePackItems(state, enabledPacks);
    }

    /// <summary>TS <c>createGameState(project, options)</c> with the options object.</summary>
    public static GameState CreateGameState(GameProject project, CreateGameStateOptions options) =>
        CreateGameState(project, options.Seed);

    /// <summary>TS <c>createGameState</c> options (<c>{ seed?: string }</c>).</summary>
    public sealed record CreateGameStateOptions(string? Seed = null);

    /// <summary>
    /// Write a running GameState back into the project (persistence bridge —
    /// keeps the single project store and the editor views in sync while
    /// the engine owns play-mode rules).
    /// </summary>
    public static GameProject ApplyStateToProject(GameProject project, GameState state)
    {
        var eventFlags = new OrderedDictionary<string, bool>();
        foreach (var (key, value) in state.Flags) eventFlags[key] = Js.Truthy(value);

        return project with
        {
            // Generated scenes (mine floors) sync through so rendering works while
            // the player stands in one; they are dropped again on exitMine and are
            // hidden from editor scene lists (scene.generated flag).
            Scenes = state.World.Scenes,
            Npcs = (project.Npcs ?? []).Select(npc =>
                state.Npcs.TryGetValue(npc.Id, out var npcState) && npcState is not null
                    ? npc with { X = npcState.X, Y = npcState.Y, SceneId = npcState.SceneId }
                    : npc).ToList(),
            Quests = (project.Quests ?? []).Select(quest =>
            {
                if (!state.Quests.TryGetValue(quest.Id, out var progress) || progress is null) return quest;
                return quest with
                {
                    Status = progress.Status,
                    Objectives = quest.Objectives.Select(objective =>
                        progress.Objectives.TryGetValue(objective.Id, out var objProgress) && objProgress is not null
                            ? objective with { Progress = objProgress.Progress, Completed = objProgress.Completed }
                            : objective).ToList(),
                };
            }).ToList(),
            Player = project.Player with
            {
                X = state.Player.X,
                Y = state.Player.Y,
                Direction = state.Player.Direction,
                SceneId = state.Player.SceneId,
                Inventory = state.Player.Inventory,
                MaxInventorySize = state.Player.MaxInventorySize,
                Money = state.Player.Money,
                Energy = state.Player.Energy,
                MaxEnergy = state.Player.MaxEnergy,
                Skills = state.Player.Skills,
                ActiveQuests = state.Player.ActiveQuests,
                CompletedQuests = state.Player.CompletedQuests,
                EquippedTool = state.Player.EquippedTool,
            },
            EventFlags = eventFlags,
            Animals = state.Animals,
            SocialState = state.Social,
            QuarantinedItems = state.QuarantinedItems,
            CurrentWeatherId = state.Clock.WeatherId,
            MineDeepestFloor = state.Mine.DeepestFloor,
            CurrentDay = state.Clock.Day,
            CurrentSeason = state.Clock.Season,
            CurrentTimeMinutes = state.Clock.TimeMinutes,
            CurrentYear = state.Clock.Year,
            RngState = state.Rng,
        };
    }

    /// <summary>
    /// TS <c>ProjectSettingsSchema.safeParse(project.settings ?? {})</c>: the typed
    /// settings when they satisfy the schema's refinements, otherwise
    /// <c>DEFAULT_PROJECT_SETTINGS</c>.
    /// </summary>
    private static ProjectSettings ResolveSettings(ProjectSettings? settings)
    {
        if (settings is null) return SettingsSchema.DefaultProjectSettings;
        return IsValidSettings(settings) ? settings : SettingsSchema.DefaultProjectSettings;
    }

    /// <summary>zod <c>z.number()</c> rejects NaN (but accepts ±Infinity).</summary>
    private static bool IsNumber(double value) => !double.IsNaN(value);

    private static bool IsInt(double value) => Js.IsInteger(value);

    private static bool IsValidSettings(ProjectSettings s)
    {
        if (s.Movement is null || !IsNumber(s.Movement.PlayerSpeed) || !(s.Movement.PlayerSpeed > 0)) return false;
        if (!(s.MaxEnergy > 0)) return false;
        if (!(s.CollapseEnergyFraction >= 0 && s.CollapseEnergyFraction <= 1)) return false;
        if (!(s.CollapseMoneyPenalty >= 0)) return false;
        if (s.Time is null) return false;
        if (!IsInt(s.Time.DayStartMinute) || !IsInt(s.Time.DayEndMinute)) return false;
        if (!(s.Time.MinutesPerRealSecond > 0)) return false;
        if (s.Calendar is null || s.Calendar.Seasons is null || s.Calendar.Festivals is null) return false;
        foreach (var season in s.Calendar.Seasons)
        {
            if (season is null || season.Id is null || season.Name is null) return false;
            if (!IsInt(season.Days) || !(season.Days > 0)) return false;
        }
        foreach (var festival in s.Calendar.Festivals)
        {
            if (festival is null || festival.Id is null || festival.Name is null || festival.SeasonId is null) return false;
            if (!IsInt(festival.Day) || !(festival.Day > 0)) return false;
        }
        if (s.SkillLevelCurve is null || s.SkillLevelCurve.Any(value => !IsNumber(value))) return false;
        if (s.Locale is null) return false;
        return true;
    }

    /// <summary>TS <c>WeatherConfigSchema.safeParse(config).success</c>.</summary>
    private static bool IsValidWeatherConfig(WeatherConfig config)
    {
        if (config.Types is null || config.Table is null) return false;
        foreach (var type in config.Types)
        {
            if (type is null || type.Id is null || type.Name is null) return false;
            if (!(type.CropDamageChance >= 0 && type.CropDamageChance <= 1)) return false;
        }
        foreach (var (_, entries) in config.Table)
        {
            if (entries is null) return false;
            foreach (var entry in entries)
            {
                if (entry is null || entry.WeatherId is null) return false;
                if (!(entry.Weight > 0)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// TS passes <c>CustomCropDefinition[]</c> where <c>CropDefinition[]</c> is expected
    /// (structural typing); the extra <c>customAsset</c> key rides along in <c>Extra</c>.
    /// </summary>
    private static CropDefinition ToCropDefinition(CustomCropDefinition crop) =>
        JsonSerializer.SerializeToNode(crop, JsonDefaults.Options).Deserialize<CropDefinition>(JsonDefaults.Options)!;
}
