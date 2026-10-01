namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// The engine's inventory stacking rules (`farm_sim::inventory`), for the inventories the editor
/// writes: a slot holds one item at one quality and at most the item's `maxStack` units (0 means
/// no cap), and adds stop at `maxInventorySize` slots.
module InventoryRules =
    /// The most units of `item` one slot holds (`maxStack`; 0 or less is no cap).
    let stackCap (item: Item) = if item.MaxStack <= 0.0 then infinity else item.MaxStack

    /// Adds `quantity` of `item` (normal quality) in one pass: tops up the item's slots to the
    /// cap, then opens new slots of at most one cap while there is room. Returns the inventory
    /// and the units that did not fit.
    let add (item: Item) (quantity: float) (maxSlots: float) (inventory: InventorySlot list) : InventorySlot list * float =
        let cap = stackCap item
        let mutable remaining = max 0.0 quantity
        let topped =
            inventory
            |> List.map (fun slot ->
                if remaining > 0.0 && slot.Item.Id = item.Id && slot.Quality.IsNone && slot.Quantity < cap then
                    let take = min (cap - slot.Quantity) remaining
                    remaining <- remaining - take
                    { slot with Quantity = slot.Quantity + take }
                else slot)
        let added = ResizeArray<InventorySlot>()
        while remaining > 0.0 && float (topped.Length + added.Count) < maxSlots do
            let take = min cap remaining
            added.Add({ Item = item; Quantity = take; Quality = None })
            remaining <- remaining - take
        topped @ List.ofSeq added, remaining

    /// A held (or dropped) copy of an item brought up to date with its `current` definition,
    /// keeping the instance data the copy carries: a tool's durability stays, clamped to the new
    /// maximum (`farm_sim::inventory::refresh_item`).
    let refresh (saved: Item) (current: Item) : Item =
        let durability =
            match saved.Durability, current.MaxDurability with
            | Some durability, Some maximum -> Some(min durability maximum)
            | _ -> current.Durability
        { current with Durability = durability }

/// Authoring-side load order, conflict-aware composition, localization and project import.
module PackMerge =
    let private problem id severity message = { PackId = id; Severity = severity; Message = message }

    /// Install order with dependencies before dependents. Missing dependencies and cycles stay
    /// visible as errors while the pack remains inspectable, like the web editor.
    let resolveOrder (installs: PackInstallation list) : ContentPack list * PackProblem list =
        let enabled = installs |> List.filter (fun installation -> installation.Enabled) |> List.map (fun installation -> installation.Pack)
        // JavaScript Map(entries): the last duplicate id supplies dependency lookups.
        let byId = Dictionary<string, ContentPack>()
        for pack in enabled do byId.[pack.Manifest.Id] <- pack

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
                for dependency in pack.Manifest.Dependencies do
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
            if not (PackRules.isEngineCompatible (Some pack.Manifest.EngineCompatibility) PackRules.EngineVersion) then
                problems.Add(problem pack.Manifest.Id "warning"
                    (sprintf "Pack '%s' targets engine %s; this engine is %s"
                        pack.Manifest.Id pack.Manifest.EngineCompatibility PackRules.EngineVersion))
        List.ofSeq ordered, List.ofSeq problems

    /// Appends new definitions, replaces declared overrides and warns about undeclared collisions.
    let private mergeArray
        (pack: ContentPack)
        (problems: ResizeArray<PackProblem>)
        (overrides: HashSet<string>)
        (label: string)
        (idOf: 'T -> string)
        (target: 'T list)
        (definitions: 'T list) : 'T list =
        let result = ResizeArray<'T>(target)
        for definition in definitions do
            let id = idOf definition
            let index = result.FindIndex(fun existing -> idOf existing = id)
            if index < 0 then result.Add definition
            elif overrides.Contains id then result.[index] <- definition
            else
                problems.Add(problem pack.Manifest.Id "warning"
                    (sprintf "Pack '%s' redefines %s '%s' without declaring it in manifest.overrides — keeping the earlier definition"
                        pack.Manifest.Id label id))
        List.ofSeq result

    /// Preview enabled pack layers over base content. Collection order and override semantics
    /// match the TypeScript engine, including warnings for undeclared collisions.
    let mergeIntoContent (baseContent: GameContent) (installs: PackInstallation list) : GameContent * PackProblem list =
        let packs, orderProblems = resolveOrder installs
        if List.isEmpty packs then baseContent, orderProblems
        else
            let problems = ResizeArray<PackProblem>(orderProblems)
            let merge (content: GameContent) (rawPack: ContentPack) =
                let pack = PackRules.namespacePack rawPack
                let added = pack.Content
                let overrides = HashSet<string>(pack.Manifest.Overrides)
                let mutable crops = content.Crops
                for crop in added.Crops do
                    match crops |> List.tryFindIndex (fun (id, _) -> id = crop.Id) with
                    | Some index ->
                        if overrides.Contains crop.Id then crops <- crops |> List.mapi (fun i entry -> if i = index then crop.Id, crop else entry)
                        else
                            problems.Add(problem pack.Manifest.Id "warning"
                                (sprintf "Pack '%s' redefines crop '%s' without declaring it in manifest.overrides — keeping the earlier definition"
                                    pack.Manifest.Id crop.Id))
                    | None -> crops <- crops @ [ crop.Id, crop ]
                let mergeInto label idOf target definitions = mergeArray pack problems overrides label idOf target definitions
                { content with
                    Crops = crops
                    Items = mergeInto "item" (fun (d: Item) -> d.Id) content.Items added.Items
                    Recipes = mergeInto "recipe" (fun (d: RecipeDefinition) -> d.Id) content.Recipes added.Recipes
                    MachineTypes = mergeInto "machine type" (fun (d: MachineTypeDefinition) -> d.Id) content.MachineTypes added.MachineTypes
                    NodeTypes = mergeInto "node type" (fun (d: NodeTypeDefinition) -> d.Id) content.NodeTypes added.NodeTypes
                    AnimalSpecies = mergeInto "animal species" (fun (d: AnimalSpeciesDefinition) -> d.Id) content.AnimalSpecies added.AnimalSpecies
                    FishTables = mergeInto "fish table" (fun (d: FishTable) -> d.Id) content.FishTables added.FishTables
                    Weather =
                        { content.Weather with
                            Types = mergeInto "weather type" (fun (d: WeatherTypeDefinition) -> d.Id) content.Weather.Types added.WeatherTypes }
                    Npcs = mergeInto "NPC" (fun (d: Npc) -> d.Id) content.Npcs added.Npcs
                    Dialogues = mergeInto "dialogue" (fun (d: Dialogue) -> d.Id) content.Dialogues added.Dialogues
                    Scenes = mergeInto "scene" (fun (d: Scene) -> d.Id) content.Scenes added.Scenes
                    Events = mergeInto "event" (fun (d: GameEvent) -> d.Id) content.Events added.Events
                    Quests = mergeInto "quest" (fun (d: Quest) -> d.Id) content.Quests added.Quests
                    Shops = mergeInto "shop" (fun (d: ShopDefinition) -> d.Id) content.Shops added.Shops
                    Actions = mergeInto "action" (fun (d: ActionDef) -> d.Id) content.Actions added.Actions
                    Minigames = mergeInto "minigame" (fun (d: MinigameDef) -> d.Id) content.Minigames added.Minigames }
            let merged = List.fold merge baseContent packs
            merged, List.ofSeq problems

    /// Later enabled packs override earlier translations. Empty strings are valid translations;
    /// absent keys/locales leave authored text intact.
    let applyLocaleStrings (content: GameContent) (installs: PackInstallation list) (locale: string) =
        if System.String.IsNullOrEmpty locale then content
        else
            let packs, _ = resolveOrder installs
            let table = Dictionary<string, string>()
            for rawPack in packs do
                let pack = PackRules.namespacePack rawPack
                match pack.Content.Strings |> List.tryFind (fun (key, _) -> key = locale) with
                | Some(_, values) -> for key, value in values do table.[key] <- value
                | None -> ()
            if table.Count = 0 then content
            else
                let lookup kind id field fallback =
                    match table.TryGetValue(kind + ":" + id + ":" + field) with
                    | true, value -> value
                    | _ -> fallback
                let dialogue (d: Dialogue) = { d with Text = lookup "dialogue" d.Id "text" d.Text }
                { content with
                    Items =
                        content.Items
                        |> List.map (fun i -> { i with Name = lookup "item" i.Id "name" i.Name; Description = lookup "item" i.Id "description" i.Description })
                    Quests =
                        content.Quests
                        |> List.map (fun q -> { q with Name = lookup "quest" q.Id "name" q.Name; Description = lookup "quest" q.Id "description" q.Description })
                    Dialogues = content.Dialogues |> List.map dialogue
                    Npcs =
                        content.Npcs
                        |> List.map (fun n -> { n with Name = lookup "npc" n.Id "name" n.Name; Dialogue = List.map dialogue n.Dialogue }) }

    let private customCrop (crop: CropDefinition) : CustomCropDefinition =
        { Id = crop.Id; Name = crop.Name; Visual = crop.Visual
          SeedCost = crop.SeedCost; BaseHarvestValue = crop.BaseHarvestValue
          GrowthTime = crop.GrowthTime; GrowthDays = crop.GrowthDays
          Stages = crop.Stages; Seasons = crop.Seasons
          RegrowthTime = crop.RegrowthTime; RegrowthDays = crop.RegrowthDays
          CanRegrow = crop.CanRegrow; MultiTile = crop.MultiTile
          MutationChance = crop.MutationChance; YieldMin = crop.YieldMin
          YieldMax = crop.YieldMax; HarvestItemId = crop.HarvestItemId; CustomAsset = None; Extra = crop.Extra }

    /// A pack's art (`PackRules.packAssets`) added to the project's custom assets: assets with an
    /// id the project lacks are appended in pack order. An id the project already has keeps the
    /// project's asset; the ids where the pack's asset differs are returned. Asset ids are not
    /// namespaced, so the pack's definitions find their art under the ids they were exported with.
    let mergeAssets (project: GameProject) (pack: ContentPack) : GameProject * string list =
        match PackRules.packAssets pack with
        | Error _
        | Ok [] -> project, []
        | Ok assets ->
            let known = Dictionary<string, CustomAsset>()
            for asset in project.CustomAssets do
                if not (known.ContainsKey asset.Id) then known.[asset.Id] <- asset
            let added = ResizeArray<CustomAsset>()
            let conflicts = ResizeArray<string>()
            for asset in assets do
                match known.TryGetValue asset.Id with
                | true, existing ->
                    if (existing.DataUrl <> asset.DataUrl || existing.Animations <> asset.Animations || existing.Sheet <> asset.Sheet)
                       && not (conflicts.Contains asset.Id) then
                        conflicts.Add asset.Id
                | _ ->
                    known.[asset.Id] <- asset
                    added.Add asset
            let next = if added.Count = 0 then project else { project with CustomAssets = project.CustomAssets @ List.ofSeq added }
            next, List.ofSeq conflicts

    /// The warning for art a pack brings under an id the project already uses differently.
    let assetConflict (pack: ContentPack) (assetId: string) : PackProblem =
        problem pack.Manifest.Id "warning"
            (sprintf "Pack '%s' brings art '%s', but the project already has different art with that id — keeping the project's"
                pack.Manifest.Id assetId)

    /// Materialize a pack as editable project content. The Mods editor's ImportPack edit uses
    /// this F# transform, including the pack's optional player-start inventory and location and
    /// its art (`mergeAssets`).
    let applyToProject (project: GameProject) (rawPack: ContentPack) : GameProject * PackProblem list =
        let project, assetConflicts = mergeAssets project rawPack
        let pack = PackRules.namespacePack rawPack
        let content = pack.Content
        let problems = ResizeArray<PackProblem>(assetConflicts |> List.map (assetConflict rawPack))
        let overrides = HashSet<string>(pack.Manifest.Overrides)
        let merge label idOf target definitions = mergeArray pack problems overrides label idOf target definitions
        let next =
            { project with
                CustomCrops =
                    Some(merge "crop" (fun (d: CustomCropDefinition) -> d.Id) (Option.defaultValue [] project.CustomCrops) (List.map customCrop content.Crops))
                Items = merge "item" (fun (d: Item) -> d.Id) project.Items content.Items
                Recipes = merge "recipe" (fun (d: RecipeDefinition) -> d.Id) project.Recipes content.Recipes
                MachineTypes = merge "machine type" (fun (d: MachineTypeDefinition) -> d.Id) project.MachineTypes content.MachineTypes
                NodeTypes = merge "node type" (fun (d: NodeTypeDefinition) -> d.Id) project.NodeTypes content.NodeTypes
                AnimalSpecies = merge "animal species" (fun (d: AnimalSpeciesDefinition) -> d.Id) project.AnimalSpecies content.AnimalSpecies
                FishTables = merge "fish table" (fun (d: FishTable) -> d.Id) project.FishTables content.FishTables
                Weather =
                    { project.Weather with
                        Types = merge "weather type" (fun (d: WeatherTypeDefinition) -> d.Id) project.Weather.Types content.WeatherTypes }
                Npcs = merge "NPC" (fun (d: Npc) -> d.Id) project.Npcs content.Npcs
                Dialogues = merge "dialogue" (fun (d: Dialogue) -> d.Id) project.Dialogues content.Dialogues
                Scenes = merge "scene" (fun (d: Scene) -> d.Id) project.Scenes content.Scenes
                Events = merge "event" (fun (d: GameEvent) -> d.Id) project.Events content.Events
                Quests = merge "quest" (fun (d: Quest) -> d.Id) project.Quests content.Quests
                Shops = merge "shop" (fun (d: ShopDefinition) -> d.Id) project.Shops content.Shops
                Actions = merge "action" (fun (d: ActionDef) -> d.Id) project.Actions content.Actions
                Minigames = merge "minigame" (fun (d: MinigameDef) -> d.Id) project.Minigames content.Minigames }

        match content.PlayerStart with
        | None -> next, List.ofSeq problems
        | Some start ->
            // JavaScript Map(entries): later duplicate ids win inventory resolution.
            let byItemId = Dictionary<string, Item>()
            for item in next.Items do byItemId.[item.Id] <- item
            // Merged like any other add (farm_sim::packs::apply_pack_to_project): stacks fill to
            // maxStack and the slot limit holds.
            let mutable inventory = next.Player.Inventory
            for slot in start.Inventory do
                match byItemId.TryGetValue slot.ItemId with
                | true, item ->
                    let merged, rejected = InventoryRules.add item slot.Quantity next.Player.MaxInventorySize inventory
                    inventory <- merged
                    if rejected > 0.0 then
                        problems.Add(
                            problem pack.Manifest.Id "warning"
                                (sprintf "playerStart: %g of %g× '%s' don't fit in the starting inventory (%g slots)" rejected slot.Quantity slot.ItemId next.Player.MaxInventorySize))
                | _ -> problems.Add(problem pack.Manifest.Id "error" (sprintf "playerStart references unknown item '%s'" slot.ItemId))
            let player =
                { next.Player with
                    Inventory = inventory
                    Money = defaultArg start.Money next.Player.Money
                    SceneId = defaultArg start.SceneId next.Player.SceneId
                    X = defaultArg start.X next.Player.X
                    Y = defaultArg start.Y next.Player.Y }
            let next = { next with Player = player }
            let next =
                match start.SceneId with
                | Some sceneId when sceneId <> "" -> { next with StartSceneId = sceneId }
                | _ -> next
            next, List.ofSeq problems
