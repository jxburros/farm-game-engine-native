namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// What ItemEditor "Add to inventory" does (its toasts).
[<RequireQualifiedAccess>]
type InventoryAddResult =
    | Added
    | StackFull
    | InventoryFull
    | UnknownItem

/// The content edits (NPCEditor, ItemEditor, CropEditor, QuestEditor, EventsEditor, ShopEditor,
/// RecipeEditor, NodeTypeEditor, WildlifeEditor, ActionsEditor, ProjectSettingsEditor weather and
/// mine sections, InterfaceEditor). Each function returns the same instance when nothing changed.
module internal EditContent =
    /// Add or replace by id in the list `get` reads and `set` writes; the same project when equal.
    let private upsert (get: GameProject -> 'T list) (set: GameProject -> 'T list -> GameProject) (idOf: 'T -> string) (item: 'T) (project: GameProject) =
        match Lists.upsertBy idOf item (get project) with
        | Some next -> set project next
        | None -> project

    /// Remove by id, then scrub what referenced it (`cleanup`); the same project when absent.
    let private remove (get: GameProject -> 'T list) (set: GameProject -> 'T list -> GameProject) (idOf: 'T -> string) (cleanup: string -> GameProject -> GameProject) (id: string) (project: GameProject) =
        match Lists.removeBy idOf id (get project) with
        | None -> project
        | Some kept -> set project kept |> cleanup id

    let private noCleanup (_: string) (project: GameProject) = project

    // ---- NPCs and dialogue (NPCEditor.tsx) ----

    /// `project.Dialogues` mirrors every NPC's `Dialogue` list (the web keeps both). After an NPC
    /// upsert its entries are replaced in place, removed ones dropped, new ones appended.
    let private syncDialogues (npc: Npc) (project: GameProject) =
        let ownIds = HashSet<string>(npc.Dialogue |> List.map (fun d -> d.Id))
        let byId = Dictionary<string, Dialogue>()
        for d in npc.Dialogue do byId.[d.Id] <- d
        let kept =
            project.Dialogues
            |> List.choose (fun d ->
                if ownIds.Contains d.Id then Some byId.[d.Id]
                elif d.NpcId = npc.Id then None
                else Some d)
        let present = HashSet<string>(kept |> List.map (fun d -> d.Id))
        let appended = npc.Dialogue |> List.filter (fun d -> not (present.Contains d.Id))
        let next = kept @ appended
        if next = project.Dialogues then project else { project with Dialogues = next }

    let private npcs (project: GameProject) = project.Npcs
    let private withNpcs (project: GameProject) list = { project with Npcs = list }

    /// NPCEditor `createNPC` / `updateNPC` / MovementScheduleSection `patch`.
    let upsertNpc (npc: Npc) (project: GameProject) =
        upsert npcs withNpcs (fun (n: Npc) -> n.Id) npc project |> syncDialogues npc

    /// NPCEditor `deleteNPC` (NPC + its dialogues) plus every reference to it.
    let removeNpc (npcId: string) (project: GameProject) =
        remove npcs withNpcs (fun (n: Npc) -> n.Id) Cleanup.dropNpc npcId project

    /// App.tsx NPC placement mode: `{ ...npc, x, y }` (and here the scene it is placed in).
    let moveNpc (npcId: string) (sceneId: string) (x: int) (y: int) (project: GameProject) =
        if (Proj.tryScene sceneId project).IsNone then project
        else
            let move (n: Npc) =
                if n.Id <> npcId || (n.SceneId = sceneId && n.X = float x && n.Y = float y) then n
                else { n with SceneId = sceneId; X = float x; Y = float y }
            match Lists.mapChanged move project.Npcs with
            | Some next -> { project with Npcs = next }
            | None -> project

    /// NPCDetailEditor `createDialogue` / DialogueEditor `save`: in `project.Dialogues` and on the owning NPC.
    let upsertDialogue (dialogue: Dialogue) (project: GameProject) =
        let project = upsert (fun p -> p.Dialogues) (fun p l -> { p with Dialogues = l }) (fun (d: Dialogue) -> d.Id) dialogue project
        let onNpc (n: Npc) =
            if n.Id <> dialogue.NpcId then
                // A dialogue that moved to another NPC leaves the old one.
                match Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogue.Id n.Dialogue with
                | Some kept -> { n with Dialogue = kept }
                | None -> n
            else
                match Lists.upsertBy (fun (d: Dialogue) -> d.Id) dialogue n.Dialogue with
                | Some next -> { n with Dialogue = next }
                | None -> n
        match Lists.mapChanged onNpc project.Npcs with
        | Some next -> { project with Npcs = next }
        | None -> project

    /// NPCDetailEditor `deleteDialogue`: from both lists, then chains into it end.
    let removeDialogue (dialogueId: string) (project: GameProject) =
        let project =
            match Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogueId project.Dialogues with
            | Some kept -> { project with Dialogues = kept }
            | None -> project
        let onNpc (n: Npc) =
            match Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogueId n.Dialogue with
            | Some kept -> { n with Dialogue = kept }
            | None -> n
        let project =
            match Lists.mapChanged onNpc project.Npcs with
            | Some next -> { project with Npcs = next }
            | None -> project
        Cleanup.dropDialogue dialogueId project

    // ---- Items (ItemEditor.tsx) ----

    let private items (project: GameProject) = project.Items
    let private withItems (project: GameProject) list = { project with Items = list }

    /// ItemEditor create/update. Dropped copies on tiles and inventory slots follow the definition.
    let upsertItem (item: Item) (project: GameProject) =
        let project = upsert items withItems (fun (i: Item) -> i.Id) item project
        let project =
            Proj.mapScenes (Proj.mapTiles (fun tile ->
                match tile.Item with
                | Some placed when placed.Id = item.Id && placed <> item -> { tile with Item = Some item }
                | _ -> tile)) project
        let slots =
            Lists.mapChanged
                (fun (slot: InventorySlot) -> if slot.Item.Id = item.Id && slot.Item <> item then { slot with Item = item } else slot)
                project.Player.Inventory
        match slots with
        | Some inventory -> { project with Player = { project.Player with Inventory = inventory } }
        | None -> project

    /// ItemEditor `handleDelete`. The web refuses when the item sits in the player's inventory;
    /// here the slot is removed with everything else that pointed at the item.
    let removeItem (itemId: string) (project: GameProject) =
        remove items withItems (fun (i: Item) -> i.Id) Cleanup.dropItem itemId project

    /// ItemEditor `handleAddToInventory`: stack when stackable and under the max, else a new slot
    /// when there is room; otherwise nothing happens. Also says which of its toasts applies.
    let inventoryAdd (itemId: string) (project: GameProject) : InventoryAddResult * GameProject =
        match project.Items |> List.tryFind (fun i -> i.Id = itemId) with
        | None -> InventoryAddResult.UnknownItem, project
        | Some item ->
            let inventory = project.Player.Inventory
            let next =
                match List.tryFindIndex (fun (slot: InventorySlot) -> slot.Item.Id = itemId) inventory with
                | Some index when item.Stackable ->
                    let slot = inventory.[index]
                    if slot.Quantity < item.MaxStack then Ok(List.updateAt index { slot with Quantity = slot.Quantity + 1.0 } inventory)
                    else Error InventoryAddResult.StackFull
                | _ ->
                    if float inventory.Length < project.Player.MaxInventorySize then
                        Ok(Lists.append ({ Item = item; Quantity = 1.0 } : InventorySlot) inventory)
                    else Error InventoryAddResult.InventoryFull
            match next with
            | Ok slots -> InventoryAddResult.Added, { project with Player = { project.Player with Inventory = slots } }
            | Error result -> result, project

    let addToInventory (itemId: string) (project: GameProject) = inventoryAdd itemId project |> snd

    // ---- Crops (CropEditor.tsx) ----

    /// The seed and crop items a custom crop owns (CropEditor `handleSaveCrop`).
    let cropItems (crop: CustomCropDefinition) : Item * Item =
        let seed =
            { Item.Default with
                Id = sprintf "seed-%s" crop.Id; Name = sprintf "%s Seeds" crop.Name; Description = sprintf "Plant these to grow %s" crop.Name
                Type = ItemTypes.Seed; Stackable = true; MaxStack = 999.0; Value = crop.SeedCost; CropType = Some crop.Id }
        let produce =
            { Item.Default with
                Id = sprintf "crop-%s" crop.Id; Name = crop.Name; Description = sprintf "Fresh %s" crop.Name
                Type = ItemTypes.Crop; Stackable = true; MaxStack = 999.0; Value = crop.BaseHarvestValue }
        seed, produce

    /// CropEditor save: the definition plus its seed and crop items (created or refreshed).
    let upsertCrop (crop: CustomCropDefinition) (project: GameProject) =
        let crops = defaultArg project.CustomCrops []
        let project =
            match Lists.upsertBy (fun (c: CustomCropDefinition) -> c.Id) crop crops with
            | Some next -> { project with CustomCrops = Some next }
            | None when project.CustomCrops.IsNone -> { project with CustomCrops = Some crops }
            | None -> project
        let seed, produce = cropItems crop
        project |> upsertItem seed |> upsertItem produce

    /// CropEditor delete: the definition, its seed and crop items, and what referenced them.
    let removeCrop (cropId: string) (project: GameProject) =
        match project.CustomCrops with
        | None -> project
        | Some crops ->
            match Lists.removeBy (fun (c: CustomCropDefinition) -> c.Id) cropId crops with
            | None -> project
            | Some kept ->
                { project with CustomCrops = Some kept }
                |> removeItem (sprintf "seed-%s" cropId)
                |> removeItem (sprintf "crop-%s" cropId)
                |> Cleanup.dropCrop cropId

    // ---- Quests, events, shops, recipes, nodes, machines, wildlife, actions ----

    let private quests = (fun (p: GameProject) -> p.Quests), (fun (p: GameProject) l -> { p with Quests = l }), (fun (q: Quest) -> q.Id)
    let private events = (fun (p: GameProject) -> p.Events), (fun (p: GameProject) l -> { p with Events = l }), (fun (e: GameEvent) -> e.Id)
    let private shops = (fun (p: GameProject) -> p.Shops), (fun (p: GameProject) l -> { p with Shops = l }), (fun (s: ShopDefinition) -> s.Id)
    let private recipes = (fun (p: GameProject) -> p.Recipes), (fun (p: GameProject) l -> { p with Recipes = l }), (fun (r: RecipeDefinition) -> r.Id)
    let private nodeTypes = (fun (p: GameProject) -> p.NodeTypes), (fun (p: GameProject) l -> { p with NodeTypes = l }), (fun (n: NodeTypeDefinition) -> n.Id)
    let private machineTypes = (fun (p: GameProject) -> p.MachineTypes), (fun (p: GameProject) l -> { p with MachineTypes = l }), (fun (m: MachineTypeDefinition) -> m.Id)
    let private species = (fun (p: GameProject) -> p.AnimalSpecies), (fun (p: GameProject) l -> { p with AnimalSpecies = l }), (fun (s: AnimalSpeciesDefinition) -> s.Id)
    let private animals = (fun (p: GameProject) -> p.Animals), (fun (p: GameProject) l -> { p with Animals = l }), (fun (a: AnimalState) -> a.Id)
    let private fishTables = (fun (p: GameProject) -> p.FishTables), (fun (p: GameProject) l -> { p with FishTables = l }), (fun (t: FishTable) -> t.Id)
    let private actions = (fun (p: GameProject) -> p.Actions), (fun (p: GameProject) l -> { p with Actions = l }), (fun (a: ActionDef) -> a.Id)
    let private minigames = (fun (p: GameProject) -> p.Minigames), (fun (p: GameProject) l -> { p with Minigames = l }), (fun (m: MinigameDef) -> m.Id)

    let private upsertIn (get, set, idOf) item project = upsert get set idOf item project
    let private removeIn (get, set, idOf) cleanup id project = remove get set idOf cleanup id project

    let upsertQuest (quest: Quest) (project: GameProject) = upsertIn quests quest project
    let removeQuest (questId: string) (project: GameProject) = removeIn quests Cleanup.dropQuest questId project

    let upsertEvent (event: GameEvent) (project: GameProject) = upsertIn events event project
    let removeEvent (eventId: string) (project: GameProject) = removeIn events Cleanup.dropEvent eventId project

    let upsertShop (shop: ShopDefinition) (project: GameProject) = upsertIn shops shop project
    let removeShop (shopId: string) (project: GameProject) = removeIn shops Cleanup.dropShop shopId project

    let upsertRecipe (recipe: RecipeDefinition) (project: GameProject) = upsertIn recipes recipe project
    let removeRecipe (recipeId: string) (project: GameProject) = removeIn recipes Cleanup.dropRecipe recipeId project

    let upsertNodeType (nodeType: NodeTypeDefinition) (project: GameProject) = upsertIn nodeTypes nodeType project
    let removeNodeType (nodeTypeId: string) (project: GameProject) = removeIn nodeTypes Cleanup.dropNodeType nodeTypeId project

    let upsertMachineType (machineType: MachineTypeDefinition) (project: GameProject) = upsertIn machineTypes machineType project
    let removeMachineType (machineTypeId: string) (project: GameProject) = removeIn machineTypes Cleanup.dropMachineType machineTypeId project

    let upsertAnimalSpecies (definition: AnimalSpeciesDefinition) (project: GameProject) = upsertIn species definition project
    let removeAnimalSpecies (speciesId: string) (project: GameProject) = removeIn species Cleanup.dropAnimalSpecies speciesId project

    /// App.tsx animal placement (the animal is built by `Defaults.newAnimal`); unknown species or scene → no-op.
    let upsertAnimal (animal: AnimalState) (project: GameProject) =
        if (Proj.tryScene animal.SceneId project).IsNone || not (project.AnimalSpecies |> List.exists (fun s -> s.Id = animal.SpeciesId)) then project
        else upsertIn animals animal project

    let removeAnimal (animalId: string) (project: GameProject) = removeIn animals noCleanup animalId project

    let upsertFishTable (table: FishTable) (project: GameProject) = upsertIn fishTables table project
    let removeFishTable (tableId: string) (project: GameProject) = removeIn fishTables noCleanup tableId project

    let upsertAction (action: ActionDef) (project: GameProject) = upsertIn actions action project
    let removeAction (actionId: string) (project: GameProject) = removeIn actions Cleanup.dropAction actionId project

    let upsertMinigame (minigame: MinigameDef) (project: GameProject) = upsertIn minigames minigame project
    let removeMinigame (minigameId: string) (project: GameProject) = removeIn minigames Cleanup.dropMinigame minigameId project

    // ---- Weather, mine, interface ----

    let setWeather (config: WeatherConfig) (project: GameProject) =
        if project.Weather = config then project else { project with Weather = config }

    /// ProjectSettingsEditor weather table: `weight > 0` sets the row entry, otherwise it is removed.
    let setWeatherWeight (seasonId: string) (weatherId: string) (weight: float) (project: GameProject) =
        let table = project.Weather.Table
        let current = table |> List.tryFind (fun (key, _) -> key = seasonId) |> Option.map snd |> Option.defaultValue []
        let kept = current |> List.filter (fun e -> e.WeatherId <> weatherId)
        let next = if weight > 0.0 then kept @ [ { WeatherId = weatherId; Weight = weight } ] else kept
        if next = current then project
        else
            let updated =
                if table |> List.exists (fun (key, _) -> key = seasonId) then
                    table |> List.map (fun (key, entries) -> if key = seasonId then key, next else key, entries)
                else
                    table @ [ seasonId, next ]
            { project with Weather = { project.Weather with Table = updated } }

    let setMine (config: MineConfig) (project: GameProject) =
        if project.Mine = config then project else { project with Mine = config }

    let setGamePanels (panels: GamePanel list) (project: GameProject) =
        if panels = defaultArg project.GamePanels [] then project
        else { project with GamePanels = Some panels }
