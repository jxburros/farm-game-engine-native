namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// What removing a thing takes with it. The web editors mostly only filter the list they own
/// (NPCEditor also drops the NPC's dialogues, WildlifeEditor the species' animals, CropEditor the
/// seed and crop items, ProjectSettingsEditor the festivals of a season); everything else was left
/// for the Problems panel to report. Here every `Remove*` edit scrubs the references too, so a
/// project never gains dangling ids from an edit. Each function returns the same instance when
/// there was nothing to scrub.
module Cleanup =
    let private nullable (value: string option) : objnull =
        match value with
        | Some v -> box v
        | None -> null

    let private setField (record: 'T) (name: string) (value: objnull) : 'T = Records.withValue record name value

    /// `Some list'` when `f` removed (None) or changed at least one element.
    let private chooseChanged (f: 'T -> 'T option) (items: List<'T>) : List<'T> option =
        let mutable changed = false
        let next = List<'T>(items.Count)
        for item in items do
            match f item with
            | Some mapped ->
                if not (obj.Equals(mapped, item)) then changed <- true
                next.Add mapped
            | None -> changed <- true
        if changed then Some next else None

    let private mapField (record: 'T) (name: string) (next: List<'U> option) : 'T =
        match next with
        | Some list -> setField record name (box list)
        | None -> record

    // ---- traversals over the places a reference can hide ----

    let private mapNpcs (f: Npc -> Npc) (project: GameProject) = Proj.update "Npcs" (Lists.mapChanged f project.Npcs) project
    let private mapItems (f: Item -> Item) (project: GameProject) = Proj.update "Items" (Lists.mapChanged f project.Items) project
    let private mapQuests (f: Quest -> Quest) (project: GameProject) = Proj.update "Quests" (Lists.mapChanged f project.Quests) project
    let private mapShops (f: ShopDefinition -> ShopDefinition) (project: GameProject) = Proj.update "Shops" (Lists.mapChanged f project.Shops) project
    let private mapRecipes (f: RecipeDefinition -> RecipeDefinition) (project: GameProject) = Proj.update "Recipes" (Lists.mapChanged f project.Recipes) project
    let private mapNodeTypes (f: NodeTypeDefinition -> NodeTypeDefinition) (project: GameProject) = Proj.update "NodeTypes" (Lists.mapChanged f project.NodeTypes) project
    let private mapMachineTypes (f: MachineTypeDefinition -> MachineTypeDefinition) (project: GameProject) = Proj.update "MachineTypes" (Lists.mapChanged f project.MachineTypes) project
    let private mapSpecies (f: AnimalSpeciesDefinition -> AnimalSpeciesDefinition) (project: GameProject) = Proj.update "AnimalSpecies" (Lists.mapChanged f project.AnimalSpecies) project
    let private mapFishTables (f: FishTable -> FishTable) (project: GameProject) = Proj.update "FishTables" (Lists.mapChanged f project.FishTables) project
    let private mapAssets (f: CustomAsset -> CustomAsset) (project: GameProject) = Proj.update "CustomAssets" (Lists.mapChanged f project.CustomAssets) project

    let private mapCustomCrops (f: CustomCropDefinition -> CustomCropDefinition) (project: GameProject) =
        match project.CustomCrops with
        | null -> project
        | crops -> Proj.update "CustomCrops" (Lists.mapChanged f crops) project

    let private mapPanels (f: GamePanel -> GamePanel option) (project: GameProject) =
        match project.GamePanels with
        | null -> project
        | panels -> Proj.update "GamePanels" (chooseChanged f panels) project

    let private mapTiles (f: Tile -> Tile) (project: GameProject) = Proj.mapScenes (Proj.mapTiles f) project

    /// Dialogue options wherever dialogues live (`project.Dialogues` and every `npc.Dialogue`).
    let private mapDialogueOptions (f: DialogueOption -> DialogueOption option) (project: GameProject) =
        let mapDialogue (d: Dialogue) = mapField d "Options" (chooseChanged f d.Options)
        let mapNpc (n: Npc) = mapField n "Dialogue" (Lists.mapChanged mapDialogue n.Dialogue)
        project
        |> fun p -> Proj.update "Dialogues" (Lists.mapChanged mapDialogue p.Dialogues) p
        |> mapNpcs mapNpc

    /// Outcomes wherever they run: events, actions and minigame result tiers. `None` drops the outcome.
    let private mapOutcomes (f: EventOutcome -> EventOutcome option) (project: GameProject) =
        let mapEvent (e: GameEvent) = mapField e "Outcomes" (chooseChanged f e.Outcomes)
        let mapAction (a: ActionDef) = mapField a "Outcomes" (chooseChanged f a.Outcomes)
        let mapTier (t: MinigameResultTier) = mapField t "Outcomes" (chooseChanged f t.Outcomes)
        let mapMinigame (m: MinigameDef) = mapField m "ResultTiers" (Lists.mapChanged mapTier m.ResultTiers)
        project
        |> fun p -> Proj.update "Events" (Lists.mapChanged mapEvent p.Events) p
        |> fun p -> Proj.update "Actions" (Lists.mapChanged mapAction p.Actions) p
        |> fun p -> Proj.update "Minigames" (Lists.mapChanged mapMinigame p.Minigames) p

    /// Conditions wherever they gate: events and actions. `None` drops the condition.
    let private mapConditions (f: EventCondition -> EventCondition option) (project: GameProject) =
        let mapEvent (e: GameEvent) = mapField e "Conditions" (chooseChanged f e.Conditions)
        let mapAction (a: ActionDef) = mapField a "Conditions" (chooseChanged f a.Conditions)
        project
        |> fun p -> Proj.update "Events" (Lists.mapChanged mapEvent p.Events) p
        |> fun p -> Proj.update "Actions" (Lists.mapChanged mapAction p.Actions) p

    let private dropWhen (matches: bool) (value: 'T) : 'T option = if matches then None else Some value

    /// `current` names `id` (a null reference never does).
    let private refers (current: string | null) (id: string) : bool =
        match current with
        | null -> false
        | value -> value = id

    let private clearIf (record: 'T) (name: string) (current: string | null) (id: string) : 'T =
        if refers current id then setField record name null else record

    // ---- one function per removed kind ----

    /// An item is gone: inventory slots, shop stock, recipe lines, drops, feed/product links,
    /// fish entries, quest targets and rewards, dialogue gifts, item conditions and outcomes,
    /// dropped copies on tiles, and interface entries that showed it.
    let dropItem (itemId: string) (project: GameProject) : GameProject =
        let refersTo (id: string | null) = refers id itemId
        project
        |> fun p ->
            match Lists.filterChanged (fun (slot: InventorySlot) -> slot.Item.Id <> itemId) p.Player.Inventory with
            | Some inventory -> Proj.set "Player" (box (setField p.Player "Inventory" (box inventory))) p
            | None -> p
        |> mapShops (fun s -> mapField s "Stock" (Lists.filterChanged (fun (e: ShopStockEntry) -> e.ItemId <> itemId) s.Stock))
        |> mapRecipes (fun r ->
            let r = mapField r "Inputs" (Lists.filterChanged (fun (i: RecipeIngredient) -> i.ItemId <> itemId) r.Inputs)
            mapField r "Outputs" (Lists.filterChanged (fun (i: RecipeIngredient) -> i.ItemId <> itemId) r.Outputs))
        |> mapNodeTypes (fun n -> mapField n "Drops" (Lists.filterChanged (fun (d: NodeDrop) -> d.ItemId <> itemId) n.Drops))
        |> mapMachineTypes (fun m -> clearIf m "ItemId" m.ItemId itemId)
        |> mapSpecies (fun s ->
            let s = clearIf s "FeedItemId" s.FeedItemId itemId
            if s.ProductItemId = itemId then setField s "ProductItemId" (box "") else s)
        |> mapFishTables (fun t ->
            let t = mapField t "Entries" (Lists.filterChanged (fun (e: FishTableEntry) -> e.ItemId <> itemId) t.Entries)
            clearIf t "JunkItemId" t.JunkItemId itemId)
        |> mapQuests (fun q ->
            let q = mapField q "Objectives" (Lists.mapChanged (fun (o: QuestObjective) -> clearIf o "TargetItemId" o.TargetItemId itemId) q.Objectives)
            match q.Rewards.Items with
            | null -> q
            | items ->
                match Lists.filterChanged (fun (r: QuestRewardItem) -> r.ItemId <> itemId) items with
                | Some kept -> setField q "Rewards" (box (setField q.Rewards "Items" (box kept)))
                | None -> q)
        |> mapDialogueOptions (fun o ->
            let o = if refersTo o.GiveItem then Records.withValues o [ ("GiveItem", null); ("GiveItemQuantity", null) ] else o
            Some(clearIf o "RequiresItem" o.RequiresItem itemId))
        |> mapConditions (fun c ->
            match c with
            | :? HasItemCondition as h -> dropWhen (h.ItemId = itemId) c
            | :? InventorySpaceCondition as s -> dropWhen (s.ItemId = itemId) c
            | _ -> Some c)
        |> mapOutcomes (fun o -> dropWhen ((o.Type = EventOutcomeTypes.GiveItem || o.Type = EventOutcomeTypes.TakeItem) && refersTo o.ItemId) o)
        |> mapTiles (fun t ->
            match t.Item with
            | null -> t
            | item -> if item.Id = itemId then setField t "Item" null else t)
        |> mapPanels (fun panel ->
            Some(mapField panel "Entries" (Lists.filterChanged (fun (e: GamePanelEntry) -> not (e.Kind = GamePanelEntryKinds.Item && e.Value = itemId)) panel.Entries)))

    /// An NPC is gone: its dialogues, the editor selection, quest givers and talk targets,
    /// friendship conditions, NPC outcomes and scene NPC lists.
    let dropNpc (npcId: string) (project: GameProject) : GameProject =
        project
        |> fun p -> Proj.update "Dialogues" (Lists.filterChanged (fun (d: Dialogue) -> d.NpcId <> npcId) p.Dialogues) p
        |> fun p -> if refers p.SelectedNpcId npcId then Proj.set "SelectedNpcId" null p else p
        |> mapQuests (fun q ->
            let q = clearIf q "Giver" q.Giver npcId
            mapField q "Objectives" (Lists.mapChanged (fun (o: QuestObjective) -> clearIf o "TargetNpcId" o.TargetNpcId npcId) q.Objectives))
        |> mapConditions (fun c ->
            match c with
            | :? FriendshipCondition as f -> dropWhen (f.NpcId = npcId) c
            | _ -> Some c)
        |> mapOutcomes (fun o ->
            let npcOutcome =
                o.Type = EventOutcomeTypes.SpawnNpc || o.Type = EventOutcomeTypes.RemoveNpc
                || o.Type = EventOutcomeTypes.StartDialogue || o.Type = EventOutcomeTypes.ModifyFriendship
            dropWhen (npcOutcome && refers o.NpcId npcId) o)
        |> Proj.mapScenes (fun s -> mapField s "Npcs" (Lists.filterChanged (fun (id: string) -> id <> npcId) s.Npcs))

    /// A dialogue is gone: chains into it end the conversation; `startDialogue` falls back to the NPC's first.
    let dropDialogue (dialogueId: string) (project: GameProject) : GameProject =
        project
        |> mapDialogueOptions (fun o -> Some(clearIf o "NextDialogueId" o.NextDialogueId dialogueId))
        |> mapOutcomes (fun o -> Some(if o.Type = EventOutcomeTypes.StartDialogue then clearIf o "DialogueId" o.DialogueId dialogueId else o))

    /// A quest is gone: prerequisites, offers, quest conditions and outcomes, recipe unlocks and the player's lists.
    let dropQuest (questId: string) (project: GameProject) : GameProject =
        project
        |> mapQuests (fun q ->
            match q.Prerequisites with
            | null -> q
            | prerequisites -> mapField q "Prerequisites" (Lists.filterChanged (fun (id: string) -> id <> questId) prerequisites))
        |> mapDialogueOptions (fun o -> Some(clearIf o "OfferQuestId" o.OfferQuestId questId))
        |> mapConditions (fun c ->
            match c with
            | :? QuestStatusCondition as q -> dropWhen (q.QuestId = questId) c
            | _ -> Some c)
        |> mapOutcomes (fun o -> dropWhen ((o.Type = EventOutcomeTypes.StartQuest || o.Type = EventOutcomeTypes.CompleteQuest) && refers o.QuestId questId) o)
        |> mapRecipes (fun r ->
            match r.Unlock with
            | null -> r
            | unlock -> if refers unlock.QuestId questId then setField r "Unlock" (box (setField unlock "QuestId" null)) else r)
        |> fun p ->
            let player = p.Player
            let player = mapField player "ActiveQuests" (Lists.filterChanged (fun (id: string) -> id <> questId) player.ActiveQuests)
            let player = mapField player "CompletedQuests" (Lists.filterChanged (fun (id: string) -> id <> questId) player.CompletedQuests)
            if obj.ReferenceEquals(player, p.Player) then p else Proj.set "Player" (box player) p

    /// An event is gone: scene event lists and its auto-managed fired flag.
    let dropEvent (eventId: string) (project: GameProject) : GameProject =
        let flag = EventsSchema.EventFiredFlag eventId
        project
        |> Proj.mapScenes (fun s -> mapField s "Events" (Lists.filterChanged (fun (id: string) -> id <> eventId) s.Events))
        |> fun p ->
            if p.EventFlags.ContainsKey flag then
                let flags = OrderedDictionary<string, bool>(p.EventFlags)
                flags.Remove flag |> ignore
                Proj.set "EventFlags" (box flags) p
            else p

    /// A shop is gone: dialogue options no longer open it.
    let dropShop (shopId: string) (project: GameProject) : GameProject =
        project |> mapDialogueOptions (fun o -> Some(clearIf o "OpenShopId" o.OpenShopId shopId))

    /// A recipe is gone: machines mid-way through it stop.
    let dropRecipe (recipeId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Machine with
            | null -> t
            | machine ->
                match machine.Processing with
                | null -> t
                | processing -> if processing.RecipeId = recipeId then setField t "Machine" (box (setField machine "Processing" null)) else t)

    /// A node type is gone: placed nodes of it and mine band entries.
    let dropNodeType (nodeTypeId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Node with
            | null -> t
            | node -> if node.TypeId = nodeTypeId then setField t "Node" null else t)
        |> fun p ->
            let mapBand (b: MineBand) = mapField b "Rocks" (Lists.filterChanged (fun (r: MineRockWeight) -> r.NodeTypeId <> nodeTypeId) b.Rocks)
            match Lists.mapChanged mapBand p.Mine.Bands with
            | Some bands -> Proj.set "Mine" (box (setField p.Mine "Bands" (box bands))) p
            | None -> p

    /// A machine type is gone: recipes become hand crafts and placed machines disappear.
    let dropMachineType (machineTypeId: string) (project: GameProject) : GameProject =
        project
        |> mapRecipes (fun r -> clearIf r "MachineTypeId" r.MachineTypeId machineTypeId)
        |> mapTiles (fun t ->
            match t.Machine with
            | null -> t
            | machine -> if machine.TypeId = machineTypeId then setField t "Machine" null else t)

    /// A species is gone: so are its animals (WildlifeEditor).
    let dropAnimalSpecies (speciesId: string) (project: GameProject) : GameProject =
        Proj.update "Animals" (Lists.filterChanged (fun (a: AnimalState) -> a.SpeciesId <> speciesId) project.Animals) project

    /// An action is gone: item "use" bindings, dialogue bindings, `performAction` outcomes and interface buttons.
    let dropAction (actionId: string) (project: GameProject) : GameProject =
        project
        |> mapItems (fun i -> clearIf i "UseActionId" i.UseActionId actionId)
        |> mapDialogueOptions (fun o -> Some(clearIf o "ActionId" o.ActionId actionId))
        |> mapOutcomes (fun o -> dropWhen (o.Type = EventOutcomeTypes.PerformAction && refers o.ActionId actionId) o)
        |> mapPanels (fun panel ->
            Some(mapField panel "Entries" (Lists.filterChanged (fun (e: GamePanelEntry) -> not (e.Kind = GamePanelEntryKinds.Action && e.Value = actionId)) panel.Entries)))

    /// A minigame is gone: nothing starts it any more.
    let dropMinigame (minigameId: string) (project: GameProject) : GameProject =
        project |> mapOutcomes (fun o -> dropWhen (o.Type = EventOutcomeTypes.StartMinigame && refers o.MinigameId minigameId) o)

    /// A crop definition is gone: planted crops of it, harvest objectives and seeds that grew it.
    let dropCrop (cropId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Crop with
            | null -> t
            | crop -> if crop.Type = cropId then setField t "Crop" null else t)
        |> mapQuests (fun q -> mapField q "Objectives" (Lists.mapChanged (fun (o: QuestObjective) -> clearIf o "TargetCropType" o.TargetCropType cropId) q.Objectives))
        |> mapItems (fun i -> if i.Type = ItemTypes.Seed then clearIf i "CropType" i.CropType cropId else i)

    let private visualUses (assetId: string) (visual: VisualRef | null) =
        match visual with
        | null -> false
        | v ->
            v.AssetId = assetId
            || (match v.Frame with
                | null -> false
                | frame -> refers frame.AssetId assetId)

    /// An asset is gone: every binding, custom image and animation frame that used it is cleared
    /// (the web refuses to remove art in use; here the objects just fall back to the default look).
    let dropAsset (assetId: string) (project: GameProject) : GameProject =
        let clearVisual (record: 'T) (name: string) (visual: VisualRef | null) : 'T =
            if visualUses assetId visual then setField record name null else record
        let clearLayer (visuals: TileVisuals) (layer: TileLayer) =
            match layer with
            | Background -> clearVisual visuals "Background" visuals.Background
            | Overlay -> clearVisual visuals "Overlay" visuals.Overlay
            | Object -> clearVisual visuals "Object" visuals.Object
        project
        |> fun p -> clearVisual p "PlayerVisual" p.PlayerVisual
        |> fun p -> clearVisual p "SelectedTileVisual" p.SelectedTileVisual
        |> fun p -> if refers p.PlayerCustomImage assetId then Proj.set "PlayerCustomImage" null p else p
        |> mapNpcs (fun n -> clearIf (clearVisual n "Visual" n.Visual) "CustomImage" n.CustomImage assetId)
        |> mapItems (fun i -> clearIf (clearVisual i "Visual" i.Visual) "CustomImage" i.CustomImage assetId)
        |> mapCustomCrops (fun c -> clearIf (clearVisual c "Visual" c.Visual) "CustomAsset" c.CustomAsset assetId)
        |> mapNodeTypes (fun n -> clearVisual n "Visual" n.Visual)
        |> mapSpecies (fun s -> clearVisual s "Visual" s.Visual)
        |> mapMachineTypes (fun m -> clearVisual m "Visual" m.Visual)
        |> mapTiles (fun t ->
            let t = clearIf t "CustomImage" t.CustomImage assetId
            let t =
                match t.Item with
                | null -> t
                | item -> if visualUses assetId item.Visual then setField t "Item" (box (setField item "Visual" null)) else t
            match t.Visuals with
            | null -> t
            | visuals ->
                let cleared = clearLayer (clearLayer (clearLayer visuals Background) Overlay) Object
                if obj.ReferenceEquals(cleared, visuals) then t
                elif isNull cleared.Background && isNull cleared.Overlay && isNull cleared.Object then setField t "Visuals" null
                else setField t "Visuals" (box cleared))
        |> mapAssets (fun a ->
            match a.Animations with
            | null -> a
            | clips ->
                let mapClip (clip: AnimationClip) =
                    let frames = Lists.filterChanged (fun (f: ArtFrame) -> not (refers f.AssetId assetId)) clip.Frames
                    match frames with
                    | Some kept when kept.Count = 0 -> None
                    | _ -> Some(mapField clip "Frames" frames)
                mapField a "Animations" (chooseChanged mapClip clips))

    /// A scene is gone (the scene itself is removed by the edit): doors into it, its NPCs and
    /// their dialogues, its events and animals, schedule stops, quest visits, fishing spots,
    /// the mine entrance and warps. The player start moves to the start scene.
    let dropScene (sceneId: string) (project: GameProject) : GameProject =
        let npcIds = project.Npcs |> Seq.filter (fun n -> n.SceneId = sceneId) |> Seq.map (fun n -> n.Id) |> List.ofSeq
        let eventIds = project.Events |> Seq.filter (fun e -> e.SceneId = sceneId) |> Seq.map (fun e -> e.Id) |> List.ofSeq
        project
        |> Proj.mapScenes (fun s -> mapField s "Transitions" (Lists.filterChanged (fun (t: SceneTransition) -> t.ToSceneId <> sceneId) s.Transitions))
        |> fun p -> Proj.update "Npcs" (Lists.filterChanged (fun (n: Npc) -> n.SceneId <> sceneId) p.Npcs) p
        |> fun p -> npcIds |> List.fold (fun acc id -> dropNpc id acc) p
        |> fun p -> Proj.update "Events" (Lists.filterChanged (fun (e: GameEvent) -> e.SceneId <> sceneId) p.Events) p
        |> fun p -> eventIds |> List.fold (fun acc id -> dropEvent id acc) p
        |> fun p -> Proj.update "Animals" (Lists.filterChanged (fun (a: AnimalState) -> a.SceneId <> sceneId) p.Animals) p
        |> mapNpcs (fun n ->
            match n.Schedule with
            | null -> n
            | schedule -> mapField n "Schedule" (Lists.filterChanged (fun (e: NpcScheduleEntry) -> e.SceneId <> sceneId) schedule))
        |> mapQuests (fun q -> mapField q "Objectives" (Lists.mapChanged (fun (o: QuestObjective) -> clearIf o "TargetSceneId" o.TargetSceneId sceneId) q.Objectives))
        |> mapFishTables (fun t ->
            match t.SceneIds with
            | null -> t
            | ids -> mapField t "SceneIds" (Lists.filterChanged (fun (id: string) -> id <> sceneId) ids))
        |> mapOutcomes (fun o -> dropWhen (refers o.SceneId sceneId) o)
        |> fun p -> if refers p.Mine.EntranceSceneId sceneId then Proj.set "Mine" (box (setField p.Mine "EntranceSceneId" null)) p else p
        |> fun p ->
            if p.Player.SceneId <> sceneId then p
            else
                match Proj.startScene p with
                | None -> p
                | Some start ->
                    let x = max 0 (min (int start.Width - 1) (int p.Player.X))
                    let y = max 0 (min (int start.Height - 1) (int p.Player.Y))
                    Proj.set "Player" (box (Records.withValues p.Player [ ("SceneId", box start.Id); ("X", box (float x)); ("Y", box (float y)) ])) p

    /// A calendar season is gone: festivals on it, weather rows, and season lists that named it.
    let dropSeason (seasonId: string) (project: GameProject) : GameProject =
        let without (seasons: List<string>) = Lists.filterChanged (fun (s: string) -> s <> seasonId) seasons
        let optionalList (record: 'T) (name: string) (seasons: List<string> | null) : 'T =
            match seasons with
            | null -> record
            | list ->
                match without list with
                | Some kept when kept.Count = 0 -> setField record name null
                | next -> mapField record name next
        project
        |> fun p ->
            let calendar = p.Settings.Calendar
            let calendar = mapField calendar "Festivals" (Lists.filterChanged (fun (f: CalendarFestival) -> f.SeasonId <> seasonId) calendar.Festivals)
            if obj.ReferenceEquals(calendar, p.Settings.Calendar) then p
            else Proj.set "Settings" (box (setField p.Settings "Calendar" (box calendar))) p
        |> fun p ->
            if p.Weather.Table.ContainsKey seasonId then
                let table = OrderedDictionary<string, List<WeatherTableEntry>>(p.Weather.Table)
                table.Remove seasonId |> ignore
                Proj.set "Weather" (box (setField p.Weather "Table" (box table))) p
            else p
        |> mapCustomCrops (fun c -> mapField c "Seasons" (without c.Seasons))
        |> mapShops (fun s -> mapField s "Stock" (Lists.mapChanged (fun (e: ShopStockEntry) -> optionalList e "Seasons" e.Seasons) s.Stock))
        |> mapQuests (fun q -> optionalList q "AvailableSeasons" q.AvailableSeasons)
        |> mapFishTables (fun t -> optionalList t "Seasons" t.Seasons)
        |> mapRecipes (fun r ->
            match r.Unlock with
            | null -> r
            | unlock ->
                let next = optionalList unlock "Seasons" unlock.Seasons
                if obj.ReferenceEquals(next, unlock) then r else setField r "Unlock" (box next))
        |> mapConditions (fun c ->
            match c with
            | :? SeasonCondition as s ->
                match without s.Seasons with
                | Some kept when kept.Count = 0 -> None
                | Some kept -> Some(setField s "Seasons" (box kept) :> EventCondition)
                | None -> Some c
            | _ -> Some c)
        |> fun p ->
            if p.CurrentSeason <> seasonId then p
            else
                match Seq.tryHead p.Settings.Calendar.Seasons with
                | Some first -> Proj.set "CurrentSeason" (box first.Id) p
                | None -> p
