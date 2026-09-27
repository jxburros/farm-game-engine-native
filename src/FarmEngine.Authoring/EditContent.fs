namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// The content edits (NPCEditor, ItemEditor, CropEditor, QuestEditor, EventsEditor, ShopEditor,
/// RecipeEditor, NodeTypeEditor, WildlifeEditor, ActionsEditor, ProjectSettingsEditor weather and
/// mine sections, InterfaceEditor). Each function returns the same instance when nothing changed.
module internal EditContent =
    let private setField (record: 'T) (name: string) (value: objnull) : 'T = Records.withValue record name value

    let private upsert (name: string) (idOf: 'T -> string) (item: 'T) (list: List<'T>) (project: GameProject) =
        Proj.update name (Lists.upsertBy idOf item list) project

    let private remove (name: string) (idOf: 'T -> string) (id: string) (list: List<'T>) (cleanup: string -> GameProject -> GameProject) (project: GameProject) =
        match Lists.removeBy idOf id list with
        | None -> project
        | Some kept -> Proj.set name (box kept) project |> cleanup id

    // ---- NPCs and dialogue (NPCEditor.tsx) ----

    /// `project.Dialogues` mirrors every NPC's `Dialogue` list (the web keeps both). After an NPC
    /// upsert its entries are replaced in place, removed ones dropped, new ones appended.
    let private syncDialogues (npc: Npc) (project: GameProject) =
        let ownIds = HashSet<string>(npc.Dialogue |> Seq.map (fun d -> d.Id))
        let byId = npc.Dialogue |> Seq.map (fun d -> d.Id, d) |> dict
        let kept =
            project.Dialogues
            |> Seq.choose (fun d ->
                if ownIds.Contains d.Id then Some byId[d.Id]
                elif d.NpcId = npc.Id then None
                else Some d)
            |> List.ofSeq
        let present = HashSet<string>(kept |> List.map (fun d -> d.Id))
        let appended = npc.Dialogue |> Seq.filter (fun d -> not (present.Contains d.Id)) |> List.ofSeq
        let next = kept @ appended
        if next.Length = project.Dialogues.Count && Seq.forall2 (fun (a: Dialogue) (b: Dialogue) -> obj.Equals(a, b)) next project.Dialogues then project
        else Proj.set "Dialogues" (box (Lists.ofSeq next)) project

    /// NPCEditor `createNPC` / `updateNPC` / MovementScheduleSection `patch`.
    let upsertNpc (npc: Npc) (project: GameProject) =
        upsert "Npcs" (fun (n: Npc) -> n.Id) npc project.Npcs project |> syncDialogues npc

    /// NPCEditor `deleteNPC` (NPC + its dialogues) plus every reference to it.
    let removeNpc (npcId: string) (project: GameProject) =
        remove "Npcs" (fun (n: Npc) -> n.Id) npcId project.Npcs Cleanup.dropNpc project

    /// App.tsx NPC placement mode: `{ ...npc, x, y }` (and here the scene it is placed in).
    let moveNpc (npcId: string) (sceneId: string) (x: int) (y: int) (project: GameProject) =
        if (Proj.tryScene sceneId project).IsNone then project
        else
            let move (n: Npc) =
                if n.Id <> npcId || (n.SceneId = sceneId && n.X = float x && n.Y = float y) then n
                else Records.withValues n [ ("SceneId", box sceneId); ("X", box (float x)); ("Y", box (float y)) ]
            Proj.update "Npcs" (Lists.mapChanged move project.Npcs) project

    /// NPCDetailEditor `createDialogue` / DialogueEditor `save`: in `project.Dialogues` and on the owning NPC.
    let upsertDialogue (dialogue: Dialogue) (project: GameProject) =
        let project = upsert "Dialogues" (fun (d: Dialogue) -> d.Id) dialogue project.Dialogues project
        let onNpc (n: Npc) =
            if n.Id <> dialogue.NpcId then
                // A dialogue that moved to another NPC leaves the old one.
                match Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogue.Id n.Dialogue with
                | Some kept -> setField n "Dialogue" (box kept)
                | None -> n
            else
                match Lists.upsertBy (fun (d: Dialogue) -> d.Id) dialogue n.Dialogue with
                | Some next -> setField n "Dialogue" (box next)
                | None -> n
        Proj.update "Npcs" (Lists.mapChanged onNpc project.Npcs) project

    /// NPCDetailEditor `deleteDialogue`: from both lists, then chains into it end.
    let removeDialogue (dialogueId: string) (project: GameProject) =
        let project = Proj.update "Dialogues" (Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogueId project.Dialogues) project
        let onNpc (n: Npc) =
            match Lists.removeBy (fun (d: Dialogue) -> d.Id) dialogueId n.Dialogue with
            | Some kept -> setField n "Dialogue" (box kept)
            | None -> n
        Proj.update "Npcs" (Lists.mapChanged onNpc project.Npcs) project |> Cleanup.dropDialogue dialogueId

    // ---- Items (ItemEditor.tsx) ----

    /// ItemEditor create/update. Dropped copies on tiles and inventory slots follow the definition.
    let upsertItem (item: Item) (project: GameProject) =
        let project = upsert "Items" (fun (i: Item) -> i.Id) item project.Items project
        let project =
            Proj.mapScenes (Proj.mapTiles (fun tile ->
                match tile.Item with
                | null -> tile
                | placed -> if placed.Id = item.Id && not (obj.Equals(placed, item)) then setField tile "Item" (box item) else tile)) project
        let slots = Lists.mapChanged (fun (slot: InventorySlot) -> if slot.Item.Id = item.Id && not (obj.Equals(slot.Item, item)) then setField slot "Item" (box item) else slot) project.Player.Inventory
        match slots with
        | Some inventory -> Proj.set "Player" (box (setField project.Player "Inventory" (box inventory))) project
        | None -> project

    /// ItemEditor `handleDelete`. The web refuses when the item sits in the player's inventory;
    /// here the slot is removed with everything else that pointed at the item.
    let removeItem (itemId: string) (project: GameProject) =
        remove "Items" (fun (i: Item) -> i.Id) itemId project.Items Cleanup.dropItem project

    /// ItemEditor `handleAddToInventory`: stack when stackable and under the max, else a new slot
    /// when there is room; otherwise nothing happens.
    let addToInventory (itemId: string) (project: GameProject) =
        match project.Items |> Seq.tryFind (fun i -> i.Id = itemId) with
        | None -> project
        | Some item ->
            let inventory = project.Player.Inventory
            let next =
                match Seq.tryFindIndex (fun (slot: InventorySlot) -> slot.Item.Id = itemId) inventory with
                | Some index when item.Stackable ->
                    let slot = inventory[index]
                    if slot.Quantity < item.MaxStack then
                        let slots = List<InventorySlot>(inventory)
                        slots[index] <- setField slot "Quantity" (box (slot.Quantity + 1.0))
                        Some slots
                    else None
                | _ ->
                    if float inventory.Count < project.Player.MaxInventorySize then
                        Some(Lists.append (InventorySlot(Item = item, Quantity = 1.0)) inventory)
                    else None
            match next with
            | Some slots -> Proj.set "Player" (box (setField project.Player "Inventory" (box slots))) project
            | None -> project

    // ---- Crops (CropEditor.tsx) ----

    /// The seed and crop items a custom crop owns (CropEditor `handleSaveCrop`).
    let cropItems (crop: CustomCropDefinition) : Item * Item =
        let seed =
            Item(Id = sprintf "seed-%s" crop.Id, Name = sprintf "%s Seeds" crop.Name, Description = sprintf "Plant these to grow %s" crop.Name,
                 Type = ItemTypes.Seed, Stackable = true, MaxStack = 999.0, Value = crop.SeedCost, CropType = crop.Id)
        let produce =
            Item(Id = sprintf "crop-%s" crop.Id, Name = crop.Name, Description = sprintf "Fresh %s" crop.Name,
                 Type = ItemTypes.Crop, Stackable = true, MaxStack = 999.0, Value = crop.BaseHarvestValue)
        seed, produce

    /// CropEditor save: the definition plus its seed and crop items (created or refreshed).
    let upsertCrop (crop: CustomCropDefinition) (project: GameProject) =
        let crops =
            match project.CustomCrops with
            | null -> List<CustomCropDefinition>()
            | list -> list
        let project =
            match Lists.upsertBy (fun (c: CustomCropDefinition) -> c.Id) crop crops with
            | Some next -> Proj.set "CustomCrops" (box next) project
            | None when isNull project.CustomCrops -> Proj.set "CustomCrops" (box crops) project
            | None -> project
        let seed, produce = cropItems crop
        project |> upsertItem seed |> upsertItem produce

    /// CropEditor delete: the definition, its seed and crop items, and what referenced them.
    let removeCrop (cropId: string) (project: GameProject) =
        match project.CustomCrops with
        | null -> project
        | crops ->
            match Lists.removeBy (fun (c: CustomCropDefinition) -> c.Id) cropId crops with
            | None -> project
            | Some kept ->
                Proj.set "CustomCrops" (box kept) project
                |> removeItem (sprintf "seed-%s" cropId)
                |> removeItem (sprintf "crop-%s" cropId)
                |> Cleanup.dropCrop cropId

    // ---- Quests, events, shops, recipes, nodes, machines, wildlife, actions ----

    let upsertQuest (quest: Quest) (project: GameProject) = upsert "Quests" (fun (q: Quest) -> q.Id) quest project.Quests project
    let removeQuest (questId: string) (project: GameProject) = remove "Quests" (fun (q: Quest) -> q.Id) questId project.Quests Cleanup.dropQuest project

    let upsertEvent (event: GameEvent) (project: GameProject) = upsert "Events" (fun (e: GameEvent) -> e.Id) event project.Events project
    let removeEvent (eventId: string) (project: GameProject) = remove "Events" (fun (e: GameEvent) -> e.Id) eventId project.Events Cleanup.dropEvent project

    let upsertShop (shop: ShopDefinition) (project: GameProject) = upsert "Shops" (fun (s: ShopDefinition) -> s.Id) shop project.Shops project
    let removeShop (shopId: string) (project: GameProject) = remove "Shops" (fun (s: ShopDefinition) -> s.Id) shopId project.Shops Cleanup.dropShop project

    let upsertRecipe (recipe: RecipeDefinition) (project: GameProject) = upsert "Recipes" (fun (r: RecipeDefinition) -> r.Id) recipe project.Recipes project
    let removeRecipe (recipeId: string) (project: GameProject) = remove "Recipes" (fun (r: RecipeDefinition) -> r.Id) recipeId project.Recipes Cleanup.dropRecipe project

    let upsertNodeType (nodeType: NodeTypeDefinition) (project: GameProject) = upsert "NodeTypes" (fun (n: NodeTypeDefinition) -> n.Id) nodeType project.NodeTypes project
    let removeNodeType (nodeTypeId: string) (project: GameProject) = remove "NodeTypes" (fun (n: NodeTypeDefinition) -> n.Id) nodeTypeId project.NodeTypes Cleanup.dropNodeType project

    let upsertMachineType (machineType: MachineTypeDefinition) (project: GameProject) = upsert "MachineTypes" (fun (m: MachineTypeDefinition) -> m.Id) machineType project.MachineTypes project
    let removeMachineType (machineTypeId: string) (project: GameProject) = remove "MachineTypes" (fun (m: MachineTypeDefinition) -> m.Id) machineTypeId project.MachineTypes Cleanup.dropMachineType project

    let upsertAnimalSpecies (species: AnimalSpeciesDefinition) (project: GameProject) = upsert "AnimalSpecies" (fun (s: AnimalSpeciesDefinition) -> s.Id) species project.AnimalSpecies project
    let removeAnimalSpecies (speciesId: string) (project: GameProject) = remove "AnimalSpecies" (fun (s: AnimalSpeciesDefinition) -> s.Id) speciesId project.AnimalSpecies Cleanup.dropAnimalSpecies project

    /// App.tsx animal placement (the animal is built by `Defaults.newAnimal`); unknown species or scene → no-op.
    let upsertAnimal (animal: AnimalState) (project: GameProject) =
        if (Proj.tryScene animal.SceneId project).IsNone || not (project.AnimalSpecies |> Seq.exists (fun s -> s.Id = animal.SpeciesId)) then project
        else upsert "Animals" (fun (a: AnimalState) -> a.Id) animal project.Animals project

    let removeAnimal (animalId: string) (project: GameProject) = remove "Animals" (fun (a: AnimalState) -> a.Id) animalId project.Animals (fun _ p -> p) project

    let upsertFishTable (table: FishTable) (project: GameProject) = upsert "FishTables" (fun (t: FishTable) -> t.Id) table project.FishTables project
    let removeFishTable (tableId: string) (project: GameProject) = remove "FishTables" (fun (t: FishTable) -> t.Id) tableId project.FishTables (fun _ p -> p) project

    let upsertAction (action: ActionDef) (project: GameProject) = upsert "Actions" (fun (a: ActionDef) -> a.Id) action project.Actions project
    let removeAction (actionId: string) (project: GameProject) = remove "Actions" (fun (a: ActionDef) -> a.Id) actionId project.Actions Cleanup.dropAction project

    let upsertMinigame (minigame: MinigameDef) (project: GameProject) = upsert "Minigames" (fun (m: MinigameDef) -> m.Id) minigame project.Minigames project
    let removeMinigame (minigameId: string) (project: GameProject) = remove "Minigames" (fun (m: MinigameDef) -> m.Id) minigameId project.Minigames Cleanup.dropMinigame project

    // ---- Weather, mine, interface ----

    let setWeather (config: WeatherConfig) (project: GameProject) =
        if obj.Equals(project.Weather, config) then project else Proj.set "Weather" (box config) project

    /// ProjectSettingsEditor weather table: `weight > 0` sets the row entry, otherwise it is removed.
    let setWeatherWeight (seasonId: string) (weatherId: string) (weight: float) (project: GameProject) =
        let table = project.Weather.Table
        let current = if table.ContainsKey seasonId then List.ofSeq table[seasonId] else []
        let kept = current |> List.filter (fun e -> e.WeatherId <> weatherId)
        let next = if weight > 0.0 then kept @ [ WeatherTableEntry(WeatherId = weatherId, Weight = weight) ] else kept
        if next.Length = current.Length && List.forall2 (fun (a: WeatherTableEntry) (b: WeatherTableEntry) -> obj.Equals(a, b)) next current then project
        else
            let updated = OrderedDictionary<string, List<WeatherTableEntry>>(table)
            updated[seasonId] <- Lists.ofSeq next
            Proj.set "Weather" (box (setField project.Weather "Table" (box updated))) project

    let setMine (config: MineConfig) (project: GameProject) =
        if obj.Equals(project.Mine, config) then project else Proj.set "Mine" (box config) project

    let setGamePanels (panels: GamePanel list) (project: GameProject) =
        let current =
            match project.GamePanels with
            | null -> []
            | list -> List.ofSeq list
        if panels.Length = current.Length && List.forall2 (fun (a: GamePanel) (b: GamePanel) -> obj.Equals(a, b)) panels current then project
        else Proj.set "GamePanels" (box (Lists.ofSeq panels)) project
