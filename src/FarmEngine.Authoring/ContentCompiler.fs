namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open System.Text.Json
open FarmEngine.Json
open FarmEngine.Schemas

/// Project -> immutable compatibility content. Gameplay state is created by Rust; authored
/// defaults, pack composition and locale selection are resolved here before cartridge writing.
module ContentCompiler =
    let private orEmpty (items: List<'T> | null) = match items with null -> List<'T>() | xs -> xs
    let private present value = not (obj.ReferenceEquals(value, null))
    let private integer value = Double.IsFinite value && Math.Truncate value = value

    let mergeCrops (custom: List<CustomCropDefinition> | null) =
        let crops = Builtin.crops ()
        for crop in orEmpty custom do
            // Preserve customAsset and passthrough fields, just like the structural TS type.
            crops[crop.Id] <- JsonSerializer.Deserialize<CropDefinition>(JsonDefaults.ToElement crop, JsonDefaults.Options) |> nonNull
        crops

    let private validSettings (s: ProjectSettings) =
        present s && present s.Movement && not (Double.IsNaN s.Movement.PlayerSpeed) && s.Movement.PlayerSpeed > 0.0
        && s.MaxEnergy > 0.0 && s.CollapseEnergyFraction >= 0.0 && s.CollapseEnergyFraction <= 1.0
        && s.CollapseMoneyPenalty >= 0.0 && present s.Time
        && integer s.Time.DayStartMinute && integer s.Time.DayEndMinute && s.Time.MinutesPerRealSecond > 0.0
        && present s.Calendar && present s.Calendar.Seasons && present s.Calendar.Festivals
        && (s.Calendar.Seasons |> Seq.forall (fun season ->
            present season && present season.Id && present season.Name && integer season.Days && season.Days > 0.0))
        && (s.Calendar.Festivals |> Seq.forall (fun festival ->
            present festival && present festival.Id && present festival.Name && present festival.SeasonId
            && integer festival.Day && festival.Day > 0.0))
        && present s.SkillLevelCurve && (s.SkillLevelCurve |> Seq.forall (Double.IsNaN >> not)) && present s.Locale

    let private validWeather (weather: WeatherConfig) =
        present weather && present weather.Types && present weather.Table
        && (weather.Types |> Seq.forall (fun t ->
            present t && present t.Id && present t.Name && t.CropDamageChance >= 0.0 && t.CropDamageChance <= 1.0))
        && (weather.Table.Values |> Seq.forall (fun entries ->
            present entries && (entries |> Seq.forall (fun entry -> present entry && present entry.WeatherId && entry.Weight > 0.0))))

    /// The project's items, or the built-in catalog when it has none.
    let items (project: GameProject) =
        if present project.Items && project.Items.Count > 0 then project.Items else Builtin.items ()

    /// Built-in and mine node types the project does not replace, then the project's own.
    let nodeTypes (project: GameProject) =
        let nodeTypes = orEmpty project.NodeTypes
        let ids = HashSet<string>(nodeTypes |> Seq.map (fun d -> d.Id))
        List<NodeTypeDefinition>(seq {
            yield! Builtin.nodeTypes () |> Seq.filter (fun d -> not (ids.Contains d.Id))
            yield! Builtin.mineNodeTypes () |> Seq.filter (fun d -> not (ids.Contains d.Id))
            yield! nodeTypes })

    /// The project's settings, or the defaults when they would not load.
    let settings (project: GameProject) =
        if validSettings project.Settings then project.Settings else SettingsSchema.DefaultProjectSettings

    /// The project's weather, or the default weather when it would not load.
    let weather (project: GameProject) =
        if validWeather project.Weather then project.Weather else MigrationsSchema.DefaultWeatherConfig()

    let baseContent (project: GameProject) =
        GameContent(
            ContentVersion = GameContentSchema.CurrentContentVersion,
            Crops = mergeCrops project.CustomCrops,
            Items = items project,
            Npcs = orEmpty project.Npcs, Dialogues = orEmpty project.Dialogues,
            Quests = orEmpty project.Quests, Events = orEmpty project.Events, Shops = orEmpty project.Shops,
            NodeTypes = nodeTypes project,
            Settings = settings project,
            Recipes = orEmpty project.Recipes, MachineTypes = orEmpty project.MachineTypes,
            Weather = weather project,
            AnimalSpecies = orEmpty project.AnimalSpecies, FishTables = orEmpty project.FishTables,
            Mine = (if present project.Mine then project.Mine else MineConfig(Enabled = false)),
            Actions = orEmpty project.Actions, Minigames = orEmpty project.Minigames,
            Scenes = orEmpty project.Scenes, StartSceneId = project.StartSceneId)

    let compile (project: GameProject) =
        let installs = orEmpty project.ContentPacks
        let merged, _ = PackMerge.mergeIntoContent (baseContent project) installs
        PackMerge.applyLocaleStrings merged installs merged.Settings.Locale
