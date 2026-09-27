namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Core
open FarmEngine.Schemas

/// Authoring-side content pack load order and conflict-aware merge. The C# engine still owns
/// runtime pack state; this module owns the project Problems preview and will feed the cartridge
/// compiler. Namespacing currently delegates to the compatibility implementation in Core until
/// the schema records are moved to F#.
module PackMerge =
    let private orEmpty (items: seq<'T> | null) : seq<'T> =
        match items with
        | null -> Seq.empty
        | items -> items

    let private problem id severity message = PackProblem(id, severity, message)

    /// Install order with dependencies before dependents. Missing dependencies and cycles stay
    /// visible as errors while the pack remains inspectable, like the web editor.
    let resolveOrder (installs: List<PackInstallation>) : ContentPack list * PackProblem list =
        let enabled =
            orEmpty installs
            |> Seq.filter (fun installation -> installation.Enabled)
            |> Seq.map (fun installation -> installation.Pack)
            |> Seq.toList
        // JavaScript Map(entries): the last duplicate id supplies dependency lookups.
        let byId = Dictionary<string, ContentPack>()
        for pack in enabled do byId[pack.Manifest.Id] <- pack

        let ordered = ResizeArray<ContentPack>()
        let problems = ResizeArray<PackProblem>()
        let visiting = HashSet<string>()
        let doneIds = HashSet<string>()

        let rec visit (pack: ContentPack) =
            let id = pack.Manifest.Id
            if doneIds.Contains id then ()
            elif visiting.Contains id then
                problems.Add(problem id "error" (sprintf "Dependency cycle involving pack '%s'" id))
            else
                visiting.Add id |> ignore
                for dependency in orEmpty pack.Manifest.Dependencies do
                    match byId.TryGetValue dependency.PackId with
                    | true, target -> visit target
                    | _ ->
                        problems.Add(problem id "error"
                            (sprintf "Pack '%s' depends on '%s' which is not installed/enabled" id dependency.PackId))
                visiting.Remove id |> ignore
                doneIds.Add id |> ignore
                ordered.Add pack

        for pack in enabled do visit pack
        for pack in ordered do
            if not (PacksSchema.IsEngineCompatible pack.Manifest.EngineCompatibility) then
                problems.Add(problem pack.Manifest.Id "warning"
                    (sprintf "Pack '%s' targets engine %s; this engine is %s"
                        pack.Manifest.Id pack.Manifest.EngineCompatibility PacksSchema.EngineVersion))
        List.ofSeq ordered, List.ofSeq problems

    let private mergeArray
        (pack: ContentPack)
        (problems: ResizeArray<PackProblem>)
        (overrides: HashSet<string>)
        (label: string)
        (idOf: 'T -> string)
        (target: List<'T>)
        (definitions: List<'T>) =
        for definition in orEmpty definitions do
            let id = idOf definition
            let index = target.FindIndex(fun existing -> idOf existing = id)
            if index < 0 then target.Add definition
            elif overrides.Contains id then target[index] <- definition
            else
                problems.Add(problem pack.Manifest.Id "warning"
                    (sprintf "Pack '%s' redefines %s '%s' without declaring it in manifest.overrides — keeping the earlier definition"
                        pack.Manifest.Id label id))

    /// Preview enabled pack layers over base content. Collection order and override semantics
    /// match the TypeScript engine, including warnings for undeclared collisions.
    let mergeIntoContent (baseContent: GameContent) (installs: List<PackInstallation>) : GameContent * PackProblem list =
        let packs, orderProblems = resolveOrder installs
        if List.isEmpty packs then baseContent, orderProblems
        else
            let problems = ResizeArray<PackProblem>(orderProblems)
            let crops = OrderedDictionary<string, CropDefinition>(baseContent.Crops)
            let items = List<Item>(baseContent.Items)
            let recipes = List<RecipeDefinition>(baseContent.Recipes)
            let machineTypes = List<MachineTypeDefinition>(baseContent.MachineTypes)
            let nodeTypes = List<NodeTypeDefinition>(baseContent.NodeTypes)
            let animalSpecies = List<AnimalSpeciesDefinition>(baseContent.AnimalSpecies)
            let fishTables = List<FishTable>(baseContent.FishTables)
            let weatherTypes = List<WeatherTypeDefinition>(baseContent.Weather.Types)
            let npcs = List<Npc>(baseContent.Npcs)
            let dialogues = List<Dialogue>(baseContent.Dialogues)
            let scenes = List<Scene>(baseContent.Scenes)
            let events = List<GameEvent>(baseContent.Events)
            let quests = List<Quest>(baseContent.Quests)
            let shops = List<ShopDefinition>(baseContent.Shops)
            let actions = List<ActionDef>(baseContent.Actions)
            let minigames = List<MinigameDef>(baseContent.Minigames)

            for rawPack in packs do
                let pack = Packs.NamespacePack rawPack
                let content = pack.Content
                let overrides = HashSet<string>(orEmpty pack.Manifest.Overrides)
                for crop in orEmpty content.Crops do
                    if crops.ContainsKey crop.Id then
                        if overrides.Contains crop.Id then crops[crop.Id] <- crop
                        else
                            problems.Add(problem pack.Manifest.Id "warning"
                                (sprintf "Pack '%s' redefines crop '%s' without declaring it in manifest.overrides — keeping the earlier definition"
                                    pack.Manifest.Id crop.Id))
                    else crops[crop.Id] <- crop
                mergeArray pack problems overrides "item" (fun (d: Item) -> d.Id) items content.Items
                mergeArray pack problems overrides "recipe" (fun (d: RecipeDefinition) -> d.Id) recipes content.Recipes
                mergeArray pack problems overrides "machine type" (fun (d: MachineTypeDefinition) -> d.Id) machineTypes content.MachineTypes
                mergeArray pack problems overrides "node type" (fun (d: NodeTypeDefinition) -> d.Id) nodeTypes content.NodeTypes
                mergeArray pack problems overrides "animal species" (fun (d: AnimalSpeciesDefinition) -> d.Id) animalSpecies content.AnimalSpecies
                mergeArray pack problems overrides "fish table" (fun (d: FishTable) -> d.Id) fishTables content.FishTables
                mergeArray pack problems overrides "weather type" (fun (d: WeatherTypeDefinition) -> d.Id) weatherTypes content.WeatherTypes
                mergeArray pack problems overrides "NPC" (fun (d: Npc) -> d.Id) npcs content.Npcs
                mergeArray pack problems overrides "dialogue" (fun (d: Dialogue) -> d.Id) dialogues content.Dialogues
                mergeArray pack problems overrides "scene" (fun (d: Scene) -> d.Id) scenes content.Scenes
                mergeArray pack problems overrides "event" (fun (d: GameEvent) -> d.Id) events content.Events
                mergeArray pack problems overrides "quest" (fun (d: Quest) -> d.Id) quests content.Quests
                mergeArray pack problems overrides "shop" (fun (d: ShopDefinition) -> d.Id) shops content.Shops
                mergeArray pack problems overrides "action" (fun (d: ActionDef) -> d.Id) actions content.Actions
                mergeArray pack problems overrides "minigame" (fun (d: MinigameDef) -> d.Id) minigames content.Minigames

            let weather = Records.withValue baseContent.Weather "Types" (box weatherTypes)
            let merged =
                Records.withValues baseContent
                    [ "Crops", box crops
                      "Items", box items
                      "Recipes", box recipes
                      "MachineTypes", box machineTypes
                      "NodeTypes", box nodeTypes
                      "AnimalSpecies", box animalSpecies
                      "FishTables", box fishTables
                      "Weather", box weather
                      "Npcs", box npcs
                      "Dialogues", box dialogues
                      "Scenes", box scenes
                      "Events", box events
                      "Quests", box quests
                      "Shops", box shops
                      "Actions", box actions
                      "Minigames", box minigames ]
            merged, List.ofSeq problems

    let private customCrop (crop: CropDefinition) : CustomCropDefinition =
        CustomCropDefinition(
            Id = crop.Id, Name = crop.Name, Visual = crop.Visual,
            SeedCost = crop.SeedCost, BaseHarvestValue = crop.BaseHarvestValue,
            GrowthTime = crop.GrowthTime, GrowthDays = crop.GrowthDays,
            Stages = crop.Stages, Seasons = crop.Seasons,
            RegrowthTime = crop.RegrowthTime, RegrowthDays = crop.RegrowthDays,
            CanRegrow = crop.CanRegrow, MultiTile = crop.MultiTile,
            MutationChance = crop.MutationChance, YieldMin = crop.YieldMin,
            YieldMax = crop.YieldMax, Extra = crop.Extra)

    /// Materialize a pack as editable project content. The Mods editor's ImportPack edit uses
    /// this F# transform, including the pack's optional player-start inventory and location.
    let applyToProject (project: GameProject) (rawPack: ContentPack) : GameProject * PackProblem list =
        let pack = Packs.NamespacePack rawPack
        let content = pack.Content
        let problems = ResizeArray<PackProblem>()
        let overrides = HashSet<string>(orEmpty pack.Manifest.Overrides)
        let crops = List<CustomCropDefinition>(orEmpty project.CustomCrops)
        let items = List<Item>(project.Items)
        let recipes = List<RecipeDefinition>(orEmpty project.Recipes)
        let machineTypes = List<MachineTypeDefinition>(orEmpty project.MachineTypes)
        let nodeTypes = List<NodeTypeDefinition>(orEmpty project.NodeTypes)
        let animalSpecies = List<AnimalSpeciesDefinition>(orEmpty project.AnimalSpecies)
        let fishTables = List<FishTable>(orEmpty project.FishTables)
        let weather = if obj.ReferenceEquals(project.Weather, null) then WeatherConfig() else project.Weather
        let weatherTypes = List<WeatherTypeDefinition>(orEmpty weather.Types)
        let npcs = List<Npc>(project.Npcs)
        let dialogues = List<Dialogue>(project.Dialogues)
        let scenes = List<Scene>(project.Scenes)
        let events = List<GameEvent>(project.Events)
        let quests = List<Quest>(project.Quests)
        let shops = List<ShopDefinition>(orEmpty project.Shops)
        let actions = List<ActionDef>(orEmpty project.Actions)
        let minigames = List<MinigameDef>(orEmpty project.Minigames)

        let cropDefinitions = List<CustomCropDefinition>(orEmpty content.Crops |> Seq.map customCrop)
        mergeArray pack problems overrides "crop" (fun (d: CustomCropDefinition) -> d.Id) crops cropDefinitions
        mergeArray pack problems overrides "item" (fun (d: Item) -> d.Id) items content.Items
        mergeArray pack problems overrides "recipe" (fun (d: RecipeDefinition) -> d.Id) recipes content.Recipes
        mergeArray pack problems overrides "machine type" (fun (d: MachineTypeDefinition) -> d.Id) machineTypes content.MachineTypes
        mergeArray pack problems overrides "node type" (fun (d: NodeTypeDefinition) -> d.Id) nodeTypes content.NodeTypes
        mergeArray pack problems overrides "animal species" (fun (d: AnimalSpeciesDefinition) -> d.Id) animalSpecies content.AnimalSpecies
        mergeArray pack problems overrides "fish table" (fun (d: FishTable) -> d.Id) fishTables content.FishTables
        mergeArray pack problems overrides "weather type" (fun (d: WeatherTypeDefinition) -> d.Id) weatherTypes content.WeatherTypes
        mergeArray pack problems overrides "NPC" (fun (d: Npc) -> d.Id) npcs content.Npcs
        mergeArray pack problems overrides "dialogue" (fun (d: Dialogue) -> d.Id) dialogues content.Dialogues
        mergeArray pack problems overrides "scene" (fun (d: Scene) -> d.Id) scenes content.Scenes
        mergeArray pack problems overrides "event" (fun (d: GameEvent) -> d.Id) events content.Events
        mergeArray pack problems overrides "quest" (fun (d: Quest) -> d.Id) quests content.Quests
        mergeArray pack problems overrides "shop" (fun (d: ShopDefinition) -> d.Id) shops content.Shops
        mergeArray pack problems overrides "action" (fun (d: ActionDef) -> d.Id) actions content.Actions
        mergeArray pack problems overrides "minigame" (fun (d: MinigameDef) -> d.Id) minigames content.Minigames

        let updatedWeather = Records.withValue weather "Types" (box weatherTypes)
        let next =
            Records.withValues project
                [ "CustomCrops", box crops
                  "Items", box items
                  "Recipes", box recipes
                  "MachineTypes", box machineTypes
                  "NodeTypes", box nodeTypes
                  "AnimalSpecies", box animalSpecies
                  "FishTables", box fishTables
                  "Weather", box updatedWeather
                  "Npcs", box npcs
                  "Dialogues", box dialogues
                  "Scenes", box scenes
                  "Events", box events
                  "Quests", box quests
                  "Shops", box shops
                  "Actions", box actions
                  "Minigames", box minigames ]

        match content.PlayerStart with
        | null -> next, List.ofSeq problems
        | start ->
            // JavaScript Map(entries): later duplicate ids win inventory resolution.
            let byItemId = Dictionary<string, Item>()
            for item in next.Items do byItemId[item.Id] <- item
            let inventory = List<InventorySlot>(next.Player.Inventory)
            for slot in orEmpty start.Inventory do
                match byItemId.TryGetValue slot.ItemId with
                | true, item -> inventory.Add(InventorySlot(Item = item, Quantity = slot.Quantity))
                | _ -> problems.Add(problem pack.Manifest.Id "error" (sprintf "playerStart references unknown item '%s'" slot.ItemId))
            let changes : (string * objnull) list =
                [ "Inventory", box inventory
                  "Money", box (if start.Money.HasValue then start.Money.Value else next.Player.Money)
                  "SceneId", box (match start.SceneId with null -> next.Player.SceneId | id -> id)
                  "X", box (if start.X.HasValue then start.X.Value else next.Player.X)
                  "Y", box (if start.Y.HasValue then start.Y.Value else next.Player.Y) ]
            let player = Records.withValues next.Player changes
            let next = Records.withValue next "Player" (box player)
            let next =
                if System.String.IsNullOrEmpty start.SceneId then next
                else Records.withValue next "StartSceneId" (box start.SceneId)
            next, List.ofSeq problems
