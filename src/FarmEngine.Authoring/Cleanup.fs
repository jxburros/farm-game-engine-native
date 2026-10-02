namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// What removing a thing takes with it. The web editors mostly only filter the list they own
/// (NPCEditor also drops the NPC's dialogues, WildlifeEditor the species' animals, CropEditor the
/// seed and crop items, ProjectSettingsEditor the festivals of a season); everything else was left
/// for the Problems panel to report. Here every `Remove*` edit scrubs the references too, except
/// the ones that gate something (#86): a condition on the removed item, NPC, quest or season, a
/// dialogue option's required item, a quest prerequisite, a recipe's unlock quest, and a season
/// list it was the only entry of. Dropping those would make the gated content available to
/// everyone; kept, the dangling reference never holds (nobody has the missing item, the missing
/// quest never completes, the season never comes), so the content stays locked and Problems
/// reports the reference until the creator decides. Each function returns the same instance when
/// there was nothing to scrub.
module Cleanup =
    /// `Some list'` when `f` removed (None) or changed at least one element.
    let private chooseChanged (f: 'T -> 'T option) (items: 'T list) : 'T list option =
        let mutable any = false
        let next =
            items
            |> List.choose (fun item ->
                match f item with
                | Some mapped ->
                    if Lists.changed mapped item then any <- true
                    Some mapped
                | None ->
                    any <- true
                    None)
        if any then Some next else None

    /// `record` with a list field replaced when the helper changed it, else the same instance.
    let private withList (record: 'T) (next: 'U list option) (set: 'T -> 'U list -> 'T) : 'T =
        match next with
        | Some list -> set record list
        | None -> record

    // ---- traversals over the places a reference can hide ----

    let private mapNpcs f (project: GameProject) = withList project (Lists.mapChanged f project.Npcs) (fun p l -> { p with Npcs = l })
    let private mapItems f (project: GameProject) = withList project (Lists.mapChanged f project.Items) (fun p l -> { p with Items = l })
    let private mapQuests f (project: GameProject) = withList project (Lists.mapChanged f project.Quests) (fun p l -> { p with Quests = l })
    let private mapShops f (project: GameProject) = withList project (Lists.mapChanged f project.Shops) (fun p l -> { p with Shops = l })
    let private mapRecipes f (project: GameProject) = withList project (Lists.mapChanged f project.Recipes) (fun p l -> { p with Recipes = l })
    let private mapNodeTypes f (project: GameProject) = withList project (Lists.mapChanged f project.NodeTypes) (fun p l -> { p with NodeTypes = l })
    let private mapMachineTypes f (project: GameProject) = withList project (Lists.mapChanged f project.MachineTypes) (fun p l -> { p with MachineTypes = l })
    let private mapSpecies f (project: GameProject) = withList project (Lists.mapChanged f project.AnimalSpecies) (fun p l -> { p with AnimalSpecies = l })
    let private mapFishTables f (project: GameProject) = withList project (Lists.mapChanged f project.FishTables) (fun p l -> { p with FishTables = l })
    let private mapAssets f (project: GameProject) = withList project (Lists.mapChanged f project.CustomAssets) (fun p l -> { p with CustomAssets = l })

    let private mapCustomCrops (f: CustomCropDefinition -> CustomCropDefinition) (project: GameProject) =
        match project.CustomCrops with
        | None -> project
        | Some crops -> withList project (Lists.mapChanged f crops) (fun p l -> { p with CustomCrops = Some l })

    let private mapPanels (f: GamePanel -> GamePanel option) (project: GameProject) =
        match project.GamePanels with
        | None -> project
        | Some panels -> withList project (chooseChanged f panels) (fun p l -> { p with GamePanels = Some l })

    let private mapTiles (f: Tile -> Tile) (project: GameProject) = Proj.mapScenes (Proj.mapTiles f) project

    /// Dialogue options wherever dialogues live (`project.Dialogues` and every `npc.Dialogue`).
    let private mapDialogueOptions (f: DialogueOption -> DialogueOption option) (project: GameProject) =
        let mapDialogue (d: Dialogue) = withList d (chooseChanged f d.Options) (fun d l -> { d with Options = l })
        let mapNpc (n: Npc) = withList n (Lists.mapChanged mapDialogue n.Dialogue) (fun n l -> { n with Dialogue = l })
        let project = withList project (Lists.mapChanged mapDialogue project.Dialogues) (fun p l -> { p with Dialogues = l })
        mapNpcs mapNpc project

    /// Outcomes wherever they run: events, actions and minigame result tiers. `None` drops the outcome.
    let private mapOutcomes (f: EventOutcome -> EventOutcome option) (project: GameProject) =
        let mapEvent (e: GameEvent) = withList e (chooseChanged f e.Outcomes) (fun e l -> { e with Outcomes = l })
        let mapAction (a: ActionDef) = withList a (chooseChanged f a.Outcomes) (fun a l -> { a with Outcomes = l })
        let mapTier (t: MinigameResultTier) = withList t (chooseChanged f t.Outcomes) (fun t l -> { t with Outcomes = l })
        let mapMinigame (m: MinigameDef) = withList m (Lists.mapChanged mapTier m.ResultTiers) (fun m l -> { m with ResultTiers = l })
        let project = withList project (Lists.mapChanged mapEvent project.Events) (fun p l -> { p with Events = l })
        let project = withList project (Lists.mapChanged mapAction project.Actions) (fun p l -> { p with Actions = l })
        withList project (Lists.mapChanged mapMinigame project.Minigames) (fun p l -> { p with Minigames = l })

    /// Conditions wherever they gate: events and actions. `None` drops the condition.
    let private mapConditions (f: EventCondition -> EventCondition option) (project: GameProject) =
        let mapEvent (e: GameEvent) = withList e (chooseChanged f e.Conditions) (fun e l -> { e with Conditions = l })
        let mapAction (a: ActionDef) = withList a (chooseChanged f a.Conditions) (fun a l -> { a with Conditions = l })
        let project = withList project (Lists.mapChanged mapEvent project.Events) (fun p l -> { p with Events = l })
        withList project (Lists.mapChanged mapAction project.Actions) (fun p l -> { p with Actions = l })

    let private dropWhen (matches: bool) (value: 'T) : 'T option = if matches then None else Some value

    /// `current` names `id`.
    let private refers (current: string option) (id: string) : bool = current = Some id

    /// `None` in place of a reference to `id`, else the reference unchanged.
    let private unless (id: string) (current: string option) : string option = if refers current id then None else current

    // ---- one function per removed kind ----

    /// An item is gone: inventory slots, shop stock, recipe lines, drops, feed/product links,
    /// fish entries, quest targets and rewards, dialogue gifts, item outcomes, dropped copies on
    /// tiles, and interface entries that showed it. Gates on it stay (`hasItem` and
    /// `inventorySpace` conditions, options that require it): they now never hold.
    let dropItem (itemId: string) (project: GameProject) : GameProject =
        let refersTo (id: string option) = refers id itemId
        project
        |> fun p ->
            match Lists.filterChanged (fun (slot: InventorySlot) -> slot.Item.Id <> itemId) p.Player.Inventory with
            | Some inventory -> { p with Player = { p.Player with Inventory = inventory } }
            | None -> p
        |> mapShops (fun s -> withList s (Lists.filterChanged (fun (e: ShopStockEntry) -> e.ItemId <> itemId) s.Stock) (fun s l -> { s with Stock = l }))
        |> mapRecipes (fun r ->
            let r = withList r (Lists.filterChanged (fun (i: RecipeIngredient) -> i.ItemId <> itemId) r.Inputs) (fun r l -> { r with Inputs = l })
            withList r (Lists.filterChanged (fun (i: RecipeIngredient) -> i.ItemId <> itemId) r.Outputs) (fun r l -> { r with Outputs = l }))
        |> mapNodeTypes (fun n -> withList n (Lists.filterChanged (fun (d: NodeDrop) -> d.ItemId <> itemId) n.Drops) (fun n l -> { n with Drops = l }))
        |> mapMachineTypes (fun m -> if refersTo m.ItemId then { m with ItemId = None } else m)
        |> mapSpecies (fun s ->
            let s = if refersTo s.FeedItemId then { s with FeedItemId = None } else s
            if s.ProductItemId = itemId then { s with ProductItemId = "" } else s)
        |> mapFishTables (fun t ->
            let t = withList t (Lists.filterChanged (fun (e: FishTableEntry) -> e.ItemId <> itemId) t.Entries) (fun t l -> { t with Entries = l })
            if refersTo t.JunkItemId then { t with JunkItemId = None } else t)
        |> mapQuests (fun q ->
            let q =
                withList q
                    (Lists.mapChanged (fun (o: QuestObjective) -> if refersTo o.TargetItemId then { o with TargetItemId = None } else o) q.Objectives)
                    (fun q l -> { q with Objectives = l })
            match q.Rewards.Items with
            | None -> q
            | Some items ->
                match Lists.filterChanged (fun (r: QuestRewardItem) -> r.ItemId <> itemId) items with
                | Some kept -> { q with Rewards = { q.Rewards with Items = Some kept } }
                | None -> q)
        |> mapDialogueOptions (fun o -> Some(if refersTo o.GiveItem then { o with GiveItem = None; GiveItemQuantity = None } else o))
        |> mapOutcomes (fun o -> dropWhen ((o.Type = EventOutcomeTypes.GiveItem || o.Type = EventOutcomeTypes.TakeItem) && refersTo o.ItemId) o)
        |> mapTiles (fun t ->
            match t.Item with
            | Some item when item.Id = itemId -> { t with Item = None }
            | _ -> t)
        |> mapPanels (fun panel ->
            Some(
                withList panel
                    (Lists.filterChanged (fun (e: GamePanelEntry) -> not (e.Kind = GamePanelEntryKinds.Item && e.Value = itemId)) panel.Entries)
                    (fun p l -> { p with Entries = l })
            ))

    /// An NPC is gone: its dialogues, the editor selection, quest givers and talk targets, NPC
    /// outcomes and scene NPC lists. Friendship conditions on it stay: they now never hold.
    let dropNpc (npcId: string) (project: GameProject) : GameProject =
        project
        |> fun p -> withList p (Lists.filterChanged (fun (d: Dialogue) -> d.NpcId <> npcId) p.Dialogues) (fun p l -> { p with Dialogues = l })
        |> fun p -> if refers p.SelectedNpcId npcId then { p with SelectedNpcId = None } else p
        |> mapQuests (fun q ->
            let q = if refers q.Giver npcId then { q with Giver = None } else q
            withList q
                (Lists.mapChanged (fun (o: QuestObjective) -> if refers o.TargetNpcId npcId then { o with TargetNpcId = None } else o) q.Objectives)
                (fun q l -> { q with Objectives = l }))
        |> mapOutcomes (fun o ->
            let npcOutcome =
                o.Type = EventOutcomeTypes.SpawnNpc || o.Type = EventOutcomeTypes.RemoveNpc
                || o.Type = EventOutcomeTypes.StartDialogue || o.Type = EventOutcomeTypes.ModifyFriendship
            dropWhen (npcOutcome && refers o.NpcId npcId) o)
        |> Proj.mapScenes (fun s -> withList s (Lists.filterChanged (fun (id: string) -> id <> npcId) s.Npcs) (fun s l -> { s with Npcs = l }))

    /// A dialogue is gone: chains into it end the conversation; `startDialogue` falls back to the NPC's first.
    let dropDialogue (dialogueId: string) (project: GameProject) : GameProject =
        project
        |> mapDialogueOptions (fun o -> Some(if refers o.NextDialogueId dialogueId then { o with NextDialogueId = None } else o))
        |> mapOutcomes (fun o ->
            Some(if o.Type = EventOutcomeTypes.StartDialogue && refers o.DialogueId dialogueId then { o with DialogueId = None } else o))

    /// A quest is gone: offers, quest outcomes and the player's lists. Gates on it stay (other
    /// quests' prerequisites, `questStatus` conditions, recipe unlocks): the quest never
    /// completes now, so what waited for it stays locked.
    let dropQuest (questId: string) (project: GameProject) : GameProject =
        project
        |> mapDialogueOptions (fun o -> Some(if refers o.OfferQuestId questId then { o with OfferQuestId = None } else o))
        |> mapOutcomes (fun o -> dropWhen ((o.Type = EventOutcomeTypes.StartQuest || o.Type = EventOutcomeTypes.CompleteQuest) && refers o.QuestId questId) o)
        |> fun p ->
            let player = p.Player
            let player = withList player (Lists.filterChanged (fun (id: string) -> id <> questId) player.ActiveQuests) (fun pl l -> { pl with ActiveQuests = l })
            let player = withList player (Lists.filterChanged (fun (id: string) -> id <> questId) player.CompletedQuests) (fun pl l -> { pl with CompletedQuests = l })
            if LanguagePrimitives.PhysicalEquality player p.Player then p else { p with Player = player }

    /// An event is gone: scene event lists and its auto-managed fired flag.
    let dropEvent (eventId: string) (project: GameProject) : GameProject =
        let flag = EventsSchema.EventFiredFlag eventId
        project
        |> Proj.mapScenes (fun s -> withList s (Lists.filterChanged (fun (id: string) -> id <> eventId) s.Events) (fun s l -> { s with Events = l }))
        |> fun p ->
            if p.EventFlags |> List.exists (fun (key, _) -> key = flag) then
                { p with EventFlags = p.EventFlags |> List.filter (fun (key, _) -> key <> flag) }
            else p

    /// A shop is gone: dialogue options no longer open it.
    let dropShop (shopId: string) (project: GameProject) : GameProject =
        project |> mapDialogueOptions (fun o -> Some(if refers o.OpenShopId shopId then { o with OpenShopId = None } else o))

    /// A recipe is gone: machines mid-way through it stop.
    let dropRecipe (recipeId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Machine with
            | Some machine ->
                match machine.Processing with
                | Some processing when processing.RecipeId = recipeId -> { t with Machine = Some { machine with Processing = None } }
                | _ -> t
            | None -> t)

    /// A node type is gone: placed nodes of it and mine band entries.
    let dropNodeType (nodeTypeId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Node with
            | Some node when node.TypeId = nodeTypeId -> { t with Node = None }
            | _ -> t)
        |> fun p ->
            let mapBand (b: MineBand) =
                withList b (Lists.filterChanged (fun (r: MineRockWeight) -> r.NodeTypeId <> nodeTypeId) b.Rocks) (fun b l -> { b with Rocks = l })
            match Lists.mapChanged mapBand p.Mine.Bands with
            | Some bands -> { p with Mine = { p.Mine with Bands = bands } }
            | None -> p

    /// A machine type is gone: recipes become hand crafts and placed machines disappear.
    let dropMachineType (machineTypeId: string) (project: GameProject) : GameProject =
        project
        |> mapRecipes (fun r -> if refers r.MachineTypeId machineTypeId then { r with MachineTypeId = None } else r)
        |> mapTiles (fun t ->
            match t.Machine with
            | Some machine when machine.TypeId = machineTypeId -> { t with Machine = None }
            | _ -> t)

    /// A species is gone: so are its animals (WildlifeEditor).
    let dropAnimalSpecies (speciesId: string) (project: GameProject) : GameProject =
        withList project (Lists.filterChanged (fun (a: AnimalState) -> a.SpeciesId <> speciesId) project.Animals) (fun p l -> { p with Animals = l })

    /// An action is gone: item "use" bindings, dialogue bindings, `performAction` outcomes and interface buttons.
    let dropAction (actionId: string) (project: GameProject) : GameProject =
        project
        |> mapItems (fun i -> if refers i.UseActionId actionId then { i with UseActionId = None } else i)
        |> mapDialogueOptions (fun o -> Some(if refers o.ActionId actionId then { o with ActionId = None } else o))
        |> mapOutcomes (fun o -> dropWhen (o.Type = EventOutcomeTypes.PerformAction && refers o.ActionId actionId) o)
        |> mapPanels (fun panel ->
            Some(
                withList panel
                    (Lists.filterChanged (fun (e: GamePanelEntry) -> not (e.Kind = GamePanelEntryKinds.Action && e.Value = actionId)) panel.Entries)
                    (fun p l -> { p with Entries = l })
            ))

    /// A minigame is gone: nothing starts it any more.
    let dropMinigame (minigameId: string) (project: GameProject) : GameProject =
        project |> mapOutcomes (fun o -> dropWhen (o.Type = EventOutcomeTypes.StartMinigame && refers o.MinigameId minigameId) o)

    /// A crop definition is gone: planted crops of it, harvest objectives and seeds that grew it.
    let dropCrop (cropId: string) (project: GameProject) : GameProject =
        project
        |> mapTiles (fun t ->
            match t.Crop with
            | Some crop when crop.Type = cropId -> { t with Crop = None }
            | _ -> t)
        |> mapQuests (fun q ->
            withList q
                (Lists.mapChanged (fun (o: QuestObjective) -> if refers o.TargetCropType cropId then { o with TargetCropType = None } else o) q.Objectives)
                (fun q l -> { q with Objectives = l }))
        |> mapItems (fun i -> if i.Type = ItemTypes.Seed && refers i.CropType cropId then { i with CropType = None } else i)

    let private visualUses (assetId: string) (visual: VisualRef option) =
        match visual with
        | None -> false
        | Some v ->
            v.AssetId = assetId
            || (match v.Frame with
                | None -> false
                | Some frame -> refers frame.AssetId assetId)

    /// An asset is gone: every binding, custom image and animation frame that used it is cleared
    /// (the web refuses to remove art in use; here the objects just fall back to the default look).
    let dropAsset (assetId: string) (project: GameProject) : GameProject =
        let clear (visual: VisualRef option) = if visualUses assetId visual then None else visual
        let unchangedVisual (visual: VisualRef option) = not (visualUses assetId visual)
        project
        |> fun p -> if unchangedVisual p.PlayerVisual then p else { p with PlayerVisual = None }
        |> fun p -> if unchangedVisual p.SelectedTileVisual then p else { p with SelectedTileVisual = None }
        |> fun p -> if refers p.PlayerCustomImage assetId then { p with PlayerCustomImage = None } else p
        |> mapNpcs (fun n ->
            if unchangedVisual n.Visual && not (refers n.CustomImage assetId) then n
            else { n with Visual = clear n.Visual; CustomImage = unless assetId n.CustomImage })
        |> mapItems (fun i ->
            if unchangedVisual i.Visual && not (refers i.CustomImage assetId) then i
            else { i with Visual = clear i.Visual; CustomImage = unless assetId i.CustomImage })
        |> mapCustomCrops (fun c ->
            if unchangedVisual c.Visual && not (refers c.CustomAsset assetId) then c
            else { c with Visual = clear c.Visual; CustomAsset = unless assetId c.CustomAsset })
        |> mapNodeTypes (fun n -> if unchangedVisual n.Visual then n else { n with Visual = None })
        |> mapSpecies (fun s -> if unchangedVisual s.Visual then s else { s with Visual = None })
        |> mapMachineTypes (fun m -> if unchangedVisual m.Visual then m else { m with Visual = None })
        |> mapTiles (fun t ->
            let t = if refers t.CustomImage assetId then { t with CustomImage = None } else t
            let t =
                match t.Item with
                | Some item when visualUses assetId item.Visual -> { t with Item = Some { item with Visual = None } }
                | _ -> t
            match t.Visuals with
            | None -> t
            | Some visuals ->
                if unchangedVisual visuals.Background && unchangedVisual visuals.Overlay && unchangedVisual visuals.Object then t
                else
                    let cleared = { Background = clear visuals.Background; Overlay = clear visuals.Overlay; Object = clear visuals.Object }
                    if cleared.Background.IsNone && cleared.Overlay.IsNone && cleared.Object.IsNone then { t with Visuals = None }
                    else { t with Visuals = Some cleared })
        |> mapAssets (fun a ->
            match a.Animations with
            | None -> a
            | Some clips ->
                let mapClip (clip: AnimationClip) =
                    match Lists.filterChanged (fun (f: ArtFrame) -> not (refers f.AssetId assetId)) clip.Frames with
                    | Some [] -> None
                    | Some kept -> Some { clip with Frames = kept }
                    | None -> Some clip
                withList a (chooseChanged mapClip clips) (fun a l -> { a with Animations = Some l }))

    /// A scene is gone (the scene itself is removed by the edit): doors into it, its NPCs and
    /// their dialogues, its events and animals, schedule stops, quest visits, fishing spots,
    /// the mine entrance and warps. The player start moves to the start scene.
    let dropScene (sceneId: string) (project: GameProject) : GameProject =
        let npcIds = project.Npcs |> List.filter (fun n -> n.SceneId = sceneId) |> List.map (fun n -> n.Id)
        let eventIds = project.Events |> List.filter (fun e -> e.SceneId = sceneId) |> List.map (fun e -> e.Id)
        project
        |> Proj.mapScenes (fun s ->
            withList s (Lists.filterChanged (fun (t: SceneTransition) -> t.ToSceneId <> sceneId) s.Transitions) (fun s l -> { s with Transitions = l }))
        |> fun p -> withList p (Lists.filterChanged (fun (n: Npc) -> n.SceneId <> sceneId) p.Npcs) (fun p l -> { p with Npcs = l })
        |> fun p -> npcIds |> List.fold (fun acc id -> dropNpc id acc) p
        |> fun p -> withList p (Lists.filterChanged (fun (e: GameEvent) -> e.SceneId <> sceneId) p.Events) (fun p l -> { p with Events = l })
        |> fun p -> eventIds |> List.fold (fun acc id -> dropEvent id acc) p
        |> fun p -> withList p (Lists.filterChanged (fun (a: AnimalState) -> a.SceneId <> sceneId) p.Animals) (fun p l -> { p with Animals = l })
        |> mapNpcs (fun n ->
            match n.Schedule with
            | None -> n
            | Some schedule ->
                withList n (Lists.filterChanged (fun (e: NpcScheduleEntry) -> e.SceneId <> sceneId) schedule) (fun n l -> { n with Schedule = Some l }))
        |> mapQuests (fun q ->
            withList q
                (Lists.mapChanged (fun (o: QuestObjective) -> if refers o.TargetSceneId sceneId then { o with TargetSceneId = None } else o) q.Objectives)
                (fun q l -> { q with Objectives = l }))
        |> mapFishTables (fun t ->
            match t.SceneIds with
            | None -> t
            | Some ids -> withList t (Lists.filterChanged (fun (id: string) -> id <> sceneId) ids) (fun t l -> { t with SceneIds = Some l }))
        |> mapOutcomes (fun o -> dropWhen (refers o.SceneId sceneId) o)
        |> fun p -> if refers p.Mine.EntranceSceneId sceneId then { p with Mine = { p.Mine with EntranceSceneId = None } } else p
        |> fun p ->
            if p.Player.SceneId <> sceneId then p
            else
                match Proj.startScene p with
                | None -> p
                | Some start ->
                    let x = max 0 (min (int start.Width - 1) (int p.Player.X))
                    let y = max 0 (min (int start.Height - 1) (int p.Player.Y))
                    { p with Player = { p.Player with SceneId = start.Id; X = float x; Y = float y } }

    /// A calendar season is gone: festivals on it, weather rows, and season lists that named it.
    /// A list it was the only entry of keeps it: an empty (or absent) list means "every season",
    /// so emptying it would open winter-only stock, quests, fish and recipes all year. Kept, the
    /// season never comes, and Problems reports it.
    let dropSeason (seasonId: string) (project: GameProject) : GameProject =
        let without (seasons: string list) =
            match Lists.filterChanged (fun (s: string) -> s <> seasonId) seasons with
            | Some [] -> None
            | other -> other
        /// An optional season list without the season.
        let optionalList (seasons: string list option) : string list option option =
            match seasons with
            | None -> None
            | Some list -> without list |> Option.map Some
        project
        |> fun p ->
            let calendar = p.Settings.Calendar
            match Lists.filterChanged (fun (f: CalendarFestival) -> f.SeasonId <> seasonId) calendar.Festivals with
            | Some festivals -> { p with Settings = { p.Settings with Calendar = { calendar with Festivals = festivals } } }
            | None -> p
        |> fun p ->
            if p.Weather.Table |> List.exists (fun (key, _) -> key = seasonId) then
                { p with Weather = { p.Weather with Table = p.Weather.Table |> List.filter (fun (key, _) -> key <> seasonId) } }
            else p
        |> mapCustomCrops (fun c -> withList c (without c.Seasons) (fun c l -> { c with Seasons = l }))
        |> mapShops (fun s ->
            withList s
                (Lists.mapChanged (fun (e: ShopStockEntry) -> match optionalList e.Seasons with Some next -> { e with Seasons = next } | None -> e) s.Stock)
                (fun s l -> { s with Stock = l }))
        |> mapQuests (fun q -> match optionalList q.AvailableSeasons with Some next -> { q with AvailableSeasons = next } | None -> q)
        |> mapFishTables (fun t -> match optionalList t.Seasons with Some next -> { t with Seasons = next } | None -> t)
        |> mapRecipes (fun r ->
            match r.Unlock with
            | Some unlock ->
                match optionalList unlock.Seasons with
                | Some next -> { r with Unlock = Some { unlock with Seasons = next } }
                | None -> r
            | None -> r)
        |> mapConditions (fun c ->
            match c with
            | EventCondition.Season s ->
                match without s.Seasons with
                | Some kept -> Some(EventCondition.Season { s with Seasons = kept })
                | None -> Some c
            | _ -> Some c)
        |> fun p ->
            if p.CurrentSeason <> seasonId then p
            else
                match List.tryHead p.Settings.Calendar.Seasons with
                | Some first -> { p with CurrentSeason = first.Id }
                | None -> p

    /// A scene shrank to `width` × `height` (#44): what stood on, or pointed at, a cut tile moves
    /// to the nearest tile inside, the way `dropScene` moves the player. That is the player start,
    /// NPCs with their schedule stops and patrol points, animals, doors landing in the scene,
    /// event tile conditions, tile-change and warp outcomes, and the mine entrance. Doors leaving
    /// from a cut tile go with the tile (moving them could stack two doors on one tile). Only
    /// the cut sides are clamped: a position that was already outside on the other side stays
    /// for Problems to report. The same instance when nothing stood outside.
    let clampToScene (sceneId: string) (width: int) (height: int) (project: GameProject) : GameProject =
        let w = float width
        let h = float height
        let cx (x: float) = if x >= w then w - 1.0 else x
        let cy (y: float) = if y >= h then h - 1.0 else y
        let cut (x: float) (y: float) = x >= w || y >= h
        let cxo (x: float option) = Option.map cx x
        let cyo (y: float option) = Option.map cy y
        let isHere (id: string option) = id = Some sceneId
        /// A tile-change or warp outcome aimed at this scene (`eventScene`: the scene a
        /// tile change without its own scene works in).
        let clampOutcome (eventScene: string) (o: EventOutcome) : EventOutcome option =
            let tileScene = match o.SceneId with Some id when id.Length > 0 -> id | _ -> eventScene
            if o.Type = EventOutcomeTypes.ChangeTile && tileScene = sceneId then
                let x, y = cxo o.TileX, cyo o.TileY
                Some(if x = o.TileX && y = o.TileY then o else { o with TileX = x; TileY = y })
            elif o.Type = EventOutcomeTypes.WarpPlayer && isHere o.SceneId then
                let x, y = cxo o.X, cyo o.Y
                Some(if x = o.X && y = o.Y then o else { o with X = x; Y = y })
            else Some o
        let clampCondition (c: EventCondition) : EventCondition option =
            match c with
            | EventCondition.EnterTile t ->
                let next = { t with X = cx t.X; Y = cy t.Y }
                Some(if next = t then c else EventCondition.EnterTile next)
            | EventCondition.InteractTile t ->
                let next = { t with X = cx t.X; Y = cy t.Y; X2 = cxo t.X2; Y2 = cyo t.Y2 }
                Some(if next = t then c else EventCondition.InteractTile next)
            | _ -> Some c
        project
        |> fun p ->
            let player = p.Player
            if player.SceneId <> sceneId || not (cut player.X player.Y) then p
            else { p with Player = { player with X = cx player.X; Y = cy player.Y } }
        |> mapNpcs (fun n ->
            let n = if n.SceneId = sceneId && cut n.X n.Y then { n with X = cx n.X; Y = cy n.Y } else n
            let n =
                match n.Schedule with
                | Some stops ->
                    match Lists.mapChanged (fun (e: NpcScheduleEntry) -> if e.SceneId = sceneId && cut e.X e.Y then { e with X = cx e.X; Y = cy e.Y } else e) stops with
                    | Some next -> { n with Schedule = Some next }
                    | None -> n
                | None -> n
            match n.PatrolPoints with
            | Some points when n.SceneId = sceneId ->
                match Lists.mapChanged (fun (pt: GridPoint) -> if cut pt.X pt.Y then { pt with X = cx pt.X; Y = cy pt.Y } else pt) points with
                | Some next -> { n with PatrolPoints = Some next }
                | None -> n
            | _ -> n)
        |> fun p ->
            match Lists.mapChanged (fun (a: AnimalState) -> if a.SceneId = sceneId && cut a.X a.Y then { a with X = cx a.X; Y = cy a.Y } else a) p.Animals with
            | Some animals -> { p with Animals = animals }
            | None -> p
        |> fun p ->
            let onScene (scene: Scene) =
                let leaving =
                    if scene.Id <> sceneId then None
                    else Lists.filterChanged (fun (t: SceneTransition) -> not (cut t.FromX t.FromY)) scene.Transitions
                let transitions = Lists.orSame scene.Transitions leaving
                let landing =
                    Lists.mapChanged
                        (fun (t: SceneTransition) -> if t.ToSceneId = sceneId && cut t.ToX t.ToY then { t with ToX = cx t.ToX; ToY = cy t.ToY } else t)
                        transitions
                match leaving, landing with
                | None, None -> scene
                | _ -> { scene with Transitions = Lists.orSame transitions landing }
            Proj.mapScenes onScene p
        |> fun p ->
            let onEvent (e: GameEvent) =
                if e.SceneId <> sceneId then e
                else
                    let e = withList e (chooseChanged clampCondition e.Conditions) (fun e l -> { e with Conditions = l })
                    withList e (chooseChanged (clampOutcome sceneId) e.Outcomes) (fun e l -> { e with Outcomes = l })
            withList p (Lists.mapChanged onEvent p.Events) (fun p l -> { p with Events = l })
        // Outcomes naming this scene themselves, wherever they run (clamping twice is harmless).
        |> mapOutcomes (clampOutcome "")
        |> fun p ->
            let mine = p.Mine
            if not (isHere mine.EntranceSceneId) then p
            else
                let x, y = cxo mine.EntranceX, cyo mine.EntranceY
                if x = mine.EntranceX && y = mine.EntranceY then p
                else { p with Mine = { mine with EntranceX = x; EntranceY = y } }
