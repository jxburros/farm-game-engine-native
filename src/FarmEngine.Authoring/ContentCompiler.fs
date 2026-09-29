namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// Project -> immutable compatibility content. Gameplay state is created by Rust; authored
/// defaults, pack composition and locale selection are resolved here before cartridge writing.
module ContentCompiler =
    let private integer (value: float) = not (Double.IsNaN value || Double.IsInfinity value) && Math.Truncate value = value

    /// Built-in crops with the project's custom crops over them (a custom crop's `customAsset`
    /// and passthrough fields ride along, like the structural TS type).
    let mergeCrops (custom: CustomCropDefinition list option) : (string * CropDefinition) list =
        let asCrop (crop: CustomCropDefinition) =
            match Decode.run SchemaJson.decodeCropDefinition (SchemaJson.encodeCustomCropDefinition crop) with
            | Ok definition -> definition
            | Error message -> invalidOp message
        (Builtin.crops (), Option.defaultValue [] custom)
        ||> List.fold (fun crops crop ->
            let definition = asCrop crop
            if crops |> List.exists (fun (id, _) -> id = crop.Id) then
                crops |> List.map (fun (id, existing) -> if id = crop.Id then id, definition else id, existing)
            else
                crops @ [ crop.Id, definition ])

    let private validSettings (s: ProjectSettings) =
        not (Double.IsNaN s.Movement.PlayerSpeed) && s.Movement.PlayerSpeed > 0.0
        && s.MaxEnergy > 0.0 && s.CollapseEnergyFraction >= 0.0 && s.CollapseEnergyFraction <= 1.0
        && s.CollapseMoneyPenalty >= 0.0
        && integer s.Time.DayStartMinute && integer s.Time.DayEndMinute && s.Time.MinutesPerRealSecond > 0.0
        && (s.Calendar.Seasons |> List.forall (fun season -> integer season.Days && season.Days > 0.0))
        && (s.Calendar.Festivals |> List.forall (fun festival -> integer festival.Day && festival.Day > 0.0))
        && (s.SkillLevelCurve |> List.forall (Double.IsNaN >> not))

    let private validWeather (weather: WeatherConfig) =
        (weather.Types |> List.forall (fun t -> t.CropDamageChance >= 0.0 && t.CropDamageChance <= 1.0))
        && (weather.Table |> List.forall (fun (_, entries) -> entries |> List.forall (fun entry -> entry.Weight > 0.0)))

    /// The project's items, or the built-in catalog when it has none.
    let items (project: GameProject) =
        if not (List.isEmpty project.Items) then project.Items else Builtin.items ()

    /// Built-in and mine node types the project does not replace, then the project's own.
    let nodeTypes (project: GameProject) =
        let ids = project.NodeTypes |> List.map (fun d -> d.Id) |> Set.ofList
        let builtIn = Builtin.nodeTypes () @ Builtin.mineNodeTypes () |> List.filter (fun d -> not (ids.Contains d.Id))
        builtIn @ project.NodeTypes

    /// The project's settings, or the defaults when they would not load.
    let settings (project: GameProject) =
        if validSettings project.Settings then project.Settings else SettingsSchema.DefaultProjectSettings

    /// The project's weather, or the default weather when it would not load.
    let weather (project: GameProject) =
        if validWeather project.Weather then project.Weather else MigrationsSchema.DefaultWeatherConfig()

    let baseContent (project: GameProject) : GameContent =
        { ContentVersion = GameContentSchema.CurrentContentVersion
          Crops = mergeCrops project.CustomCrops
          Items = items project
          Npcs = project.Npcs
          Dialogues = project.Dialogues
          Quests = project.Quests
          Events = project.Events
          Shops = project.Shops
          NodeTypes = nodeTypes project
          Settings = settings project
          Recipes = project.Recipes
          MachineTypes = project.MachineTypes
          Weather = weather project
          AnimalSpecies = project.AnimalSpecies
          FishTables = project.FishTables
          Mine = project.Mine
          Actions = project.Actions
          Minigames = project.Minigames
          Scenes = project.Scenes
          StartSceneId = project.StartSceneId }

    let compile (project: GameProject) =
        let installs = project.ContentPacks
        let merged, _ = PackMerge.mergeIntoContent (baseContent project) installs
        PackMerge.applyLocaleStrings merged installs merged.Settings.Locale
