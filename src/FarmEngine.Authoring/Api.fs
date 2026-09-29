namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// The same effective content for editor previews and cartridge exports.
[<AbstractClass; Sealed>]
type ProjectContent =
    static member Compile(project: GameProject) : GameContent = ContentCompiler.compile project

/// The C# boundary (docs/LANGUAGES.md "C# friendliness at the boundary"): static factories for
/// every `Edit` case so view models never spell out F# union syntax, with .NET collections and
/// nullable references instead of F# lists and options.
[<AbstractClass; Sealed>]
type Edits =
    static member private Cells(cells: seq<struct (int * int)>) : (int * int) list =
        cells |> Seq.map (fun (struct (x, y)) -> (x, y)) |> List.ofSeq

    static member private Visual(visual: VisualRef | null) : VisualRef option = Option.ofObj visual

    /// The layer a tile type naturally paints on (web `classifyTileType`).
    static member LayerFor(tileType: string) : TileLayer = TileRules.layerOf tileType

    static member PaintTiles(sceneId: string, layer: TileLayer, cells: seq<struct (int * int)>, tileType: string) : Edit =
        PaintTiles(sceneId, layer, Edits.Cells cells, tileType)

    /// One cell of the web single brush, on the type's natural layer.
    static member PaintTile(sceneId: string, x: int, y: int, tileType: string) : Edit =
        PaintTiles(sceneId, TileRules.layerOf tileType, [ (x, y) ], tileType)

    static member EraseLayer(sceneId: string, layer: TileLayer, cells: seq<struct (int * int)>) : Edit = EraseLayer(sceneId, layer, Edits.Cells cells)
    static member FillRect(sceneId: string, layer: TileLayer, x0: int, y0: int, x1: int, y1: int, tileType: string) : Edit = FillRect(sceneId, layer, x0, y0, x1, y1, tileType)
    static member FloodFill(sceneId: string, layer: TileLayer, x: int, y: int, tileType: string) : Edit = FloodFill(sceneId, layer, x, y, tileType)
    static member PasteTiles(sceneId: string, x: int, y: int, tiles: seq<seq<Tile>>) : Edit = PasteTiles(sceneId, x, y, tiles |> Seq.map List.ofSeq |> List.ofSeq)
    static member SetCollision(sceneId: string, cells: seq<struct (int * int)>, blocked: bool) : Edit = SetCollision(sceneId, Edits.Cells cells, blocked)
    static member SetTileVisual(sceneId: string, layer: TileLayer, cells: seq<struct (int * int)>, visual: VisualRef | null) : Edit = SetTileVisual(sceneId, layer, Edits.Cells cells, Edits.Visual visual)
    static member PlaceNode(sceneId: string, x: int, y: int, nodeTypeId: string) : Edit = PlaceNode(sceneId, x, y, nodeTypeId)
    static member RemoveNode(sceneId: string, x: int, y: int) : Edit = RemoveNode(sceneId, x, y)
    static member PlaceItem(sceneId: string, x: int, y: int, item: Item) : Edit = PlaceItem(sceneId, x, y, item)
    static member RemovePlacedItem(sceneId: string, x: int, y: int) : Edit = RemovePlacedItem(sceneId, x, y)
    static member PlaceMachine(sceneId: string, x: int, y: int, machineTypeId: string) : Edit = PlaceMachine(sceneId, x, y, machineTypeId)
    static member RemoveMachine(sceneId: string, x: int, y: int) : Edit = RemoveMachine(sceneId, x, y)
    static member ClearTile(sceneId: string, x: int, y: int) : Edit = ClearTile(sceneId, x, y)
    /// Eyedropper: the brush the tile at (x, y) picks up, or null outside the scene.
    static member PickBrush(project: GameProject, sceneId: string, x: int, y: int) : Edit | null =
        EditScenes.brushAt project sceneId x y |> Option.map SelectBrush |> Option.toObj
    static member ClearCropsAndItems(sceneId: string) : Edit = ClearCropsAndItems sceneId
    static member ResetSoil(sceneId: string) : Edit = ResetSoil sceneId
    static member FillScene(sceneId: string, tileType: string) : Edit = FillScene(sceneId, tileType)
    static member AddScene(scene: Scene) : Edit = AddScene scene
    static member RemoveScene(sceneId: string) : Edit = RemoveScene sceneId
    static member RenameScene(sceneId: string, name: string) : Edit = RenameScene(sceneId, name)
    static member ResizeScene(sceneId: string, width: int, height: int) : Edit = ResizeScene(sceneId, width, height)
    static member DuplicateScene(sceneId: string, newSceneId: string) : Edit = DuplicateScene(sceneId, newSceneId)
    static member SetTransition(sceneId: string, transition: SceneTransition) : Edit = SetTransition(sceneId, transition)
    /// TransitionEditor "Both Ways": the door and its return door as one step.
    static member LinkScenes(sceneId: string, transition: SceneTransition) : Edit = Defaults.linkScenes sceneId transition
    static member RemoveTransition(sceneId: string, fromX: int, fromY: int) : Edit = RemoveTransition(sceneId, fromX, fromY)
    static member ClearTransitions(sceneId: string) : Edit = ClearTransitions sceneId
    static member SetStartScene(sceneId: string) : Edit = SetStartScene sceneId
    static member SetPlayerStart(sceneId: string, x: int, y: int) : Edit = SetPlayerStart(sceneId, x, y)
    static member SelectBrush(tileType: string, visual: VisualRef | null) : Edit = SelectBrush(tileType, Edits.Visual visual)

    static member UpsertNpc(npc: Npc) : Edit = UpsertNpc npc
    static member RemoveNpc(npcId: string) : Edit = RemoveNpc npcId
    static member MoveNpc(npcId: string, sceneId: string, x: int, y: int) : Edit = MoveNpc(npcId, sceneId, x, y)
    static member UpsertDialogue(dialogue: Dialogue) : Edit = UpsertDialogue dialogue
    static member RemoveDialogue(dialogueId: string) : Edit = RemoveDialogue dialogueId
    static member UpsertItem(item: Item) : Edit = UpsertItem item
    static member RemoveItem(itemId: string) : Edit = RemoveItem itemId
    static member AddToInventory(itemId: string) : Edit = AddToInventory itemId
    static member UpsertCrop(crop: CustomCropDefinition) : Edit = UpsertCrop crop
    static member RemoveCrop(cropId: string) : Edit = RemoveCrop cropId
    static member UpsertQuest(quest: Quest) : Edit = UpsertQuest quest
    static member RemoveQuest(questId: string) : Edit = RemoveQuest questId
    static member UpsertEvent(event: GameEvent) : Edit = UpsertEvent event
    static member RemoveEvent(eventId: string) : Edit = RemoveEvent eventId
    static member UpsertShop(shop: ShopDefinition) : Edit = UpsertShop shop
    static member RemoveShop(shopId: string) : Edit = RemoveShop shopId
    static member UpsertRecipe(recipe: RecipeDefinition) : Edit = UpsertRecipe recipe
    static member RemoveRecipe(recipeId: string) : Edit = RemoveRecipe recipeId
    static member UpsertNodeType(nodeType: NodeTypeDefinition) : Edit = UpsertNodeType nodeType
    static member RemoveNodeType(nodeTypeId: string) : Edit = RemoveNodeType nodeTypeId
    static member UpsertMachineType(machineType: MachineTypeDefinition) : Edit = UpsertMachineType machineType
    static member RemoveMachineType(machineTypeId: string) : Edit = RemoveMachineType machineTypeId
    static member UpsertAnimalSpecies(species: AnimalSpeciesDefinition) : Edit = UpsertAnimalSpecies species
    static member RemoveAnimalSpecies(speciesId: string) : Edit = RemoveAnimalSpecies speciesId
    static member UpsertAnimal(animal: AnimalState) : Edit = UpsertAnimal animal
    static member RemoveAnimal(animalId: string) : Edit = RemoveAnimal animalId
    static member UpsertFishTable(table: FishTable) : Edit = UpsertFishTable table
    static member RemoveFishTable(tableId: string) : Edit = RemoveFishTable tableId
    static member UpsertAction(action: ActionDef) : Edit = UpsertAction action
    static member RemoveAction(actionId: string) : Edit = RemoveAction actionId
    static member UpsertMinigame(minigame: MinigameDef) : Edit = UpsertMinigame minigame
    static member RemoveMinigame(minigameId: string) : Edit = RemoveMinigame minigameId
    static member SetWeather(config: WeatherConfig) : Edit = SetWeather config
    static member SetWeatherWeight(seasonId: string, weatherId: string, weight: float) : Edit = SetWeatherWeight(seasonId, weatherId, weight)
    static member SetMine(config: MineConfig) : Edit = SetMine config
    static member SetGamePanels(panels: seq<GamePanel>) : Edit = SetGamePanels(List.ofSeq panels)

    static member SetProjectInfo(name: string, version: string) : Edit = SetProjectInfo(name, version)
    static member SetSettings(settings: ProjectSettings) : Edit = SetSettings settings
    static member SetExportSettings(settings: ExportSettings | null) : Edit = SetExportSettings(Option.ofObj settings)
    static member RemoveSeason(seasonId: string) : Edit = RemoveSeason seasonId
    /// Season arrows: swap with the season `delta` places away (festivals and weather follow by id).
    static member MoveSeason(seasonId: string, delta: int) : Edit = MoveSeason(seasonId, delta)
    static member SetGraphics(graphics: GraphicsSettings) : Edit = SetGraphics graphics
    static member SetPlayerVisual(visual: VisualRef | null) : Edit = SetPlayerVisual(Edits.Visual visual)
    static member BindPlayerVisual(visual: VisualRef | null) : Edit = BindVisual(PlayerVisual, Edits.Visual visual)
    static member BindNpcVisual(npcId: string, visual: VisualRef | null) : Edit = BindVisual(NpcVisual npcId, Edits.Visual visual)
    static member BindItemVisual(itemId: string, visual: VisualRef | null) : Edit = BindVisual(ItemVisual itemId, Edits.Visual visual)
    static member BindCropVisual(cropId: string, visual: VisualRef | null) : Edit = BindVisual(CropVisual cropId, Edits.Visual visual)
    static member BindNodeTypeVisual(nodeTypeId: string, visual: VisualRef | null) : Edit = BindVisual(NodeTypeVisual nodeTypeId, Edits.Visual visual)
    static member BindAnimalSpeciesVisual(speciesId: string, visual: VisualRef | null) : Edit = BindVisual(AnimalSpeciesVisual speciesId, Edits.Visual visual)
    static member BindMachineTypeVisual(machineTypeId: string, visual: VisualRef | null) : Edit = BindVisual(MachineTypeVisual machineTypeId, Edits.Visual visual)
    static member UpsertAsset(asset: CustomAsset) : Edit = UpsertAsset asset
    static member RemoveAsset(assetId: string) : Edit = RemoveAsset assetId
    /// One frame's duration in ticks (20 ticks = 1 second).
    static member SetFrameTicks(assetId: string, clip: string, frame: int, ticks: int) : Edit = SetFrameTicks(assetId, clip, Some frame, ticks)
    /// Every frame of a clip gets the same duration.
    static member SetAllFrameTicks(assetId: string, clip: string, ticks: int) : Edit = SetFrameTicks(assetId, clip, None, ticks)
    static member DuplicateFrame(assetId: string, clip: string, frame: int) : Edit = DuplicateFrame(assetId, clip, frame)
    static member InstallPack(pack: ContentPack) : Edit = InstallPack pack
    static member SetPackEnabled(packId: string, enabled: bool) : Edit = SetPackEnabled(packId, enabled)
    static member ReorderPacks(packIds: seq<string>) : Edit = ReorderPacks(List.ofSeq packIds)
    /// ModsEditor `move`: swap a pack with its neighbour in the load order.
    static member MovePack(project: GameProject, packId: string, delta: int) : Edit =
        let ids = project.ContentPacks |> Seq.map (fun i -> i.Pack.Manifest.Id) |> Array.ofSeq
        match Array.tryFindIndex (fun id -> id = packId) ids with
        | Some index when index + delta >= 0 && index + delta < ids.Length ->
            let swapped = Array.copy ids
            swapped.[index] <- ids.[index + delta]
            swapped.[index + delta] <- ids.[index]
            ReorderPacks(List.ofArray swapped)
        | _ -> ReorderPacks(List.ofArray ids)
    static member RemovePack(packId: string) : Edit = RemovePack packId
    static member ImportPack(packId: string) : Edit = ImportPack packId
    static member ReplaceProject(project: GameProject) : Edit = ReplaceProject project
    static member Batch(label: string, edits: seq<Edit>) : Edit = Batch(label, List.ofSeq edits)

/// `Document` for C#: create, apply, undo, redo, and drag strokes.
[<AbstractClass; Sealed>]
type Documents =
    static member UndoLimit = Document.UndoLimit
    static member Create(project: GameProject) : Document = Document.create project
    /// The project after an edit, with no history (previews).
    static member Preview(project: GameProject, edit: Edit) : GameProject = Document.run project edit
    static member Apply(document: Document, edit: Edit) : Document = Document.apply edit document
    static member ApplyInStroke(document: Document, strokeId: string, edit: Edit) : Document = Document.applyInStroke strokeId edit document
    static member EndStroke(document: Document) : Document = Document.endStroke document
    static member CanUndo(document: Document) : bool = Document.canUndo document
    static member CanRedo(document: Document) : bool = Document.canRedo document
    static member Undo(document: Document) : Document = Document.undo document
    static member Redo(document: Document) : Document = Document.redo document

/// `Problems` for C#: a read-only list with nullable-friendly members.
[<AbstractClass; Sealed>]
type Problems =
    static member Collect(project: GameProject) : IReadOnlyList<Problem> = Problems.collect project |> Array.ofList :> IReadOnlyList<Problem>
    static member BlocksExport(problems: seq<Problem>) : bool = problems |> Seq.exists (fun p -> p.IsError)

/// `Defaults` for C#: the options come back as nullable references.
[<AbstractClass; Sealed>]
type Defaults =
    static member NextId(prefix: string, existingIds: seq<string>) : string = Defaults.nextId prefix existingIds
    static member SlugId(name: string, existingIds: seq<string>, fallback: string) : string = Defaults.slugId name existingIds fallback
    static member AllIds(project: GameProject) : IReadOnlyList<string> = Defaults.allIds project |> Array.ofSeq :> IReadOnlyList<string>
    static member NewNpc(project: GameProject, name: string) : Npc = Defaults.newNpc project name
    static member NewNpc(project: GameProject, name: string, appearance: string, canMove: bool) : Npc = Defaults.newNpcWith project name appearance canMove
    static member NewDialogue(project: GameProject, npcId: string) : Dialogue = Defaults.newDialogue project npcId
    static member NewDialogueOption() : DialogueOption = Defaults.newDialogueOption ()
    static member NewScheduleEntry(npc: Npc) : NpcScheduleEntry = Defaults.newScheduleEntry npc
    static member NewItem(project: GameProject) : Item = Defaults.newItem project
    static member NewItem(project: GameProject, itemType: string) : Item = Defaults.newItemOfType project itemType
    static member NewCrop(project: GameProject) : CustomCropDefinition = Defaults.newCrop project
    static member DuplicateCrop(project: GameProject, crop: CustomCropDefinition) : CustomCropDefinition = Defaults.duplicateCrop project crop
    static member NewQuest(project: GameProject) : Quest = Defaults.newQuest project
    static member NewObjective(quest: Quest) : QuestObjective = Defaults.newObjective quest
    static member NewRewardItem(project: GameProject) : QuestRewardItem | null = Defaults.newRewardItem project |> Option.toObj
    static member DefaultCondition(kind: string, project: GameProject) : EventCondition = Defaults.defaultCondition kind project
    static member DefaultOutcome(kind: string) : EventOutcome = Defaults.defaultOutcome kind
    static member NewEvent(project: GameProject) : GameEvent = Defaults.newEvent project
    static member NewShop(project: GameProject) : ShopDefinition = Defaults.newShop project
    static member NewStockEntry(project: GameProject) : ShopStockEntry | null = Defaults.newStockEntry project |> Option.toObj
    static member NewRecipe(project: GameProject) : RecipeDefinition = Defaults.newRecipe project
    static member NewIngredient(project: GameProject) : RecipeIngredient | null = Defaults.newIngredient project |> Option.toObj
    static member NewMachineType(project: GameProject) : MachineTypeDefinition = Defaults.newMachineType project
    static member NewNodeType(project: GameProject) : NodeTypeDefinition = Defaults.newNodeType project
    static member NewDrop(project: GameProject) : NodeDrop = Defaults.newDrop project
    static member NewAnimalSpecies(project: GameProject) : AnimalSpeciesDefinition = Defaults.newAnimalSpecies project
    static member NewAnimal(project: GameProject, speciesId: string, sceneId: string, x: int, y: int) : AnimalState = Defaults.newAnimal project speciesId sceneId x y
    static member NewFishTable(project: GameProject) : FishTable = Defaults.newFishTable project
    static member NewFishEntry(project: GameProject) : FishTableEntry | null = Defaults.newFishEntry project |> Option.toObj
    static member NewAction(project: GameProject) : ActionDef = Defaults.newAction project
    static member DuplicateAction(project: GameProject, action: ActionDef) : ActionDef = Defaults.duplicateAction project action
    static member NewMinigame(project: GameProject) : MinigameDef = Defaults.newMinigame project
    static member DuplicateMinigame(project: GameProject, minigame: MinigameDef) : MinigameDef = Defaults.duplicateMinigame project minigame
    static member NewResultTier() : MinigameResultTier = Defaults.newResultTier ()
    static member NewScene(project: GameProject, name: string, width: int, height: int) : Scene = Defaults.newScene project name width height
    static member NewScene(project: GameProject, name: string, width: int, height: int, defaultTile: string) : Scene = Defaults.newSceneWith project name width height defaultTile
    static member NewTransition(toSceneId: string) : SceneTransition = Defaults.newTransition toSceneId
    static member NewSeason(calendar: CalendarConfig) : CalendarSeason = Defaults.newSeason calendar
    static member NewFestival(project: GameProject, calendar: CalendarConfig) : CalendarFestival | null = Defaults.newFestival project calendar |> Option.toObj
    static member MineEnabled(project: GameProject, enabled: bool) : MineConfig = Defaults.mineEnabled project enabled
    static member NewGamePanel(project: GameProject) : GamePanel = Defaults.newGamePanel project

/// The map's Place tools for C# (web App.tsx `handleTileClick` placement modes and the Place
/// buttons of NPCEditor, NodeTypeEditor, ItemEditor and WildlifeEditor): what each tool offers
/// and the edit a click on a tile makes. Kinds: "npc", "nodeType", "item", "machineType",
/// "animalSpecies".
[<AbstractClass; Sealed>]
type MapPlacement =
    static member private Entries(entries: seq<string * string>) : IReadOnlyList<PickerOption> =
        entries
        |> Seq.distinctBy fst
        |> Seq.map (fun (id, name) -> { Id = id; Label = (if System.String.IsNullOrWhiteSpace name then id else name); Missing = false })
        |> Array.ofSeq
        :> IReadOnlyList<PickerOption>

    /// What the Place tool of `kind` offers, in project order (node types: the built-in ones,
    /// mine rocks included, then the project's own); empty for an unknown kind.
    static member Choices(kind: string, project: GameProject) : IReadOnlyList<PickerOption> =
        match kind with
        | "npc" -> project.Npcs |> Seq.map (fun n -> n.Id, n.Name) |> MapPlacement.Entries
        | "nodeType" -> EditScenes.placeableNodeTypes project |> Seq.map (fun n -> n.Id, n.Name) |> MapPlacement.Entries
        | "item" -> project.Items |> Seq.map (fun i -> i.Id, i.Name) |> MapPlacement.Entries
        | "machineType" -> project.MachineTypes |> Seq.map (fun m -> m.Id, m.Name) |> MapPlacement.Entries
        | "animalSpecies" -> project.AnimalSpecies |> Seq.map (fun s -> s.Id, s.Name) |> MapPlacement.Entries
        | _ -> [||] :> IReadOnlyList<PickerOption>

    /// The edit a click of the Place tool of `kind` makes on (x, y): an NPC moves there, a node,
    /// item or machine is put on the tile, a newborn animal of the species is added. Null when
    /// `id` is not one of `Choices(kind, project)`.
    static member Place(project: GameProject, kind: string, id: string, sceneId: string, x: int, y: int) : Edit | null =
        let edit =
            if not (MapPlacement.Choices(kind, project) |> Seq.exists (fun o -> o.Id = id)) then None
            else
                match kind with
                | "npc" -> Some(MoveNpc(id, sceneId, x, y))
                | "nodeType" -> Some(PlaceNode(sceneId, x, y, id))
                | "item" -> Some(PlaceItem(sceneId, x, y, project.Items |> Seq.find (fun i -> i.Id = id)))
                | "machineType" -> Some(PlaceMachine(sceneId, x, y, id))
                | _ -> Some(UpsertAnimal(Defaults.newAnimal project id sceneId x y))
        Option.toObj edit

/// A form field for C#: what a property or vocabulary field holds, as strings and read-only
/// lists instead of F# unions (docs/LANGUAGES.md "C# friendliness at the boundary").
[<Sealed>]
type FormField internal (key: string, label: string, kind: string, reference: string, choices: PickerOption list,
                         optional: bool, emptyLabel: string option, placeholder: string, whenEmpty: float,
                         min: float option, max: float option, onLabel: string, offLabel: string, reason: string) =
    /// The JSON key of a vocabulary field ("itemId"); empty for a declared schema property.
    member _.Key = key
    member _.Label = label
    /// "text", "integer", "number", "bool", "choice", "reference", "referenceList",
    /// "referenceKeys" or "plain" (named like a reference but not one; see `Reason`).
    member _.Kind = kind
    /// The reference kind ("item", "npc", …) for reference fields, else "".
    member _.Reference = reference
    member _.Choices: IReadOnlyList<PickerOption> = choices |> Array.ofList :> IReadOnlyList<PickerOption>
    /// Clearing the field removes the value.
    member _.Optional = optional
    /// The label of the "nothing chosen" entry, or null when a value is required.
    member _.EmptyLabel: string | null = Option.toObj emptyLabel
    member _.Placeholder = placeholder
    /// What a cleared required number stores.
    member _.WhenEmpty = whenEmpty
    member _.HasMin = min.IsSome
    member _.Min = defaultArg min 0.0
    member _.HasMax = max.IsSome
    member _.Max = defaultArg max 0.0
    member _.OnLabel = onLabel
    member _.OffLabel = offLabel
    member _.Reason = reason

    static member internal OfVocabulary(field: VocabularyField) =
        let kind, reference, choices, onLabel, offLabel =
            match field.Kind with
            | FieldKind.Text -> "text", "", [], "", ""
            | FieldKind.Integer -> "integer", "", [], "", ""
            | FieldKind.Number -> "number", "", [], "", ""
            | FieldKind.Bool(on, off) -> "bool", "", [], on, off
            | FieldKind.OneOf choices -> "choice", "", References.choiceOptions choices, "", ""
            | FieldKind.Reference kind -> "reference", ReferenceKind.name kind, [], "", ""
            | FieldKind.ReferenceList kind -> "referenceList", ReferenceKind.name kind, [], "", ""
        FormField(field.Key, field.Label, kind, reference, choices, field.Optional, None, field.Placeholder,
                  field.WhenEmpty, field.Min, field.Max, onLabel, offLabel, "")

    static member internal OfRole(label: string, role: FieldRole) =
        let make kind reference choices empty reason =
            FormField("", label, kind, reference, choices, Option.isSome empty, empty, "", 0.0, None, None, "", "", reason)
        match role with
        | FieldRole.Reference(kind, empty) -> make "reference" (ReferenceKind.name kind) [] empty ""
        | FieldRole.ReferenceList kind -> make "referenceList" (ReferenceKind.name kind) [] None ""
        | FieldRole.ReferenceKeys kind -> make "referenceKeys" (ReferenceKind.name kind) [] None ""
        | FieldRole.OneOf(choices, empty) -> make "choice" "" (References.choiceOptions choices) empty ""
        | FieldRole.NotReference reason -> make "plain" "" [] None reason

/// Form metadata for C# content editors: what each schema property holds, the pickers' entries,
/// the condition/outcome vocabulary and the defaults of new list rows. The view maps these to
/// controls; every decision stays here.
[<AbstractClass; Sealed>]
type ContentForms =
    static member private List(xs: 'T list) = xs |> Array.ofList :> IReadOnlyList<'T>
    static member private Kind(kind: string) =
        match ReferenceKind.tryParse kind with
        | Some kind -> kind
        | None -> invalidArg (nameof kind) (sprintf "Unknown reference kind \"%s\"" kind)

    /// What `owner.property` holds (`owner` is the C# record type name), or null when the
    /// property is not declared (the form then picks a control from its type alone).
    static member Field(owner: string, property: string, label: string) : FormField | null =
        match References.roleOf owner property with
        | Some role -> FormField.OfRole(label, role)
        | None -> null

    /// The ids a picker of `kind` offers, in project order.
    static member Options(kind: string, project: GameProject) : IReadOnlyList<PickerOption> =
        References.options (ContentForms.Kind kind) project |> ContentForms.List

    /// A picker's entries for `field` holding `current`: the empty entry when allowed, a
    /// "(missing: id)" entry for an unknown id, then `available` (from `Options`, or the choices).
    static member Entries(field: FormField, available: seq<PickerOption>, current: string | null) : IReadOnlyList<PickerOption> =
        References.pickerEntries (List.ofSeq available) (Option.ofObj field.EmptyLabel) (Option.ofObj current) |> ContentForms.List

    /// Chips of a reference list: each id with its label, or "(missing: id)" when unknown.
    static member ListEntries(available: seq<PickerOption>, ids: seq<string>) : IReadOnlyList<PickerOption> =
        let known = available |> Seq.map (fun o -> o.Id, o) |> Seq.distinctBy fst |> dict
        [ for id in ids ->
              match known.TryGetValue id with
              | true, option -> option
              | _ -> { Id = id; Label = sprintf "(missing: %s)" id; Missing = true } ]
        |> ContentForms.List

    static member ConditionTypes: IReadOnlyList<PickerOption> = References.choiceOptions Vocabulary.conditionTypes |> ContentForms.List
    static member OutcomeTypes: IReadOnlyList<PickerOption> = References.choiceOptions Vocabulary.outcomeTypes |> ContentForms.List
    static member ConditionLabel(kind: string) : string = Vocabulary.conditionLabel kind
    static member OutcomeLabel(kind: string) : string = Vocabulary.outcomeLabel kind
    static member ConditionFields(kind: string) : IReadOnlyList<FormField> = Vocabulary.conditionFields kind |> List.map FormField.OfVocabulary |> ContentForms.List
    static member OutcomeFields(kind: string) : IReadOnlyList<FormField> = Vocabulary.outcomeFields kind |> List.map FormField.OfVocabulary |> ContentForms.List
    static member DefaultCondition(kind: string, project: GameProject) : EventCondition = Vocabulary.defaultCondition kind project
    static member DefaultOutcome(kind: string) : EventOutcome = Vocabulary.defaultOutcome kind

    /// Properties of `owner` to hide while its `type` is `kind` (quest objective targets).
    static member HiddenProperties(owner: string, kind: string) : IReadOnlyList<string> = Vocabulary.hiddenProperties owner kind |> ContentForms.List

    /// A new list element of `elementType` (C# type name) for the entry being edited, or null
    /// when the record's own defaults apply.
    static member NewElement(elementType: string, project: GameProject, entity: obj, siblingIds: seq<string>) : objnull =
        match Vocabulary.newElement elementType project entity (List.ofSeq siblingIds) with
        | Some element -> element
        | None -> null

/// Export settings for the Project Settings view: the values to show, the icon picker and the
/// `ChecksExport` problems of a draft.
[<AbstractClass; Sealed>]
type ExportSettingsForm =
    /// The project's export settings, or the ones export would start from.
    static member Current(project: GameProject) : ExportSettings =
        defaultArg project.Export (Defaults.newExportSettings project)

    /// Assets that can be the icon (PNG, at least 256×256), with "(none)" first and a
    /// "(missing: id)" entry when `current` is not one of them.
    static member IconOptions(project: GameProject, current: string | null) : IReadOnlyList<PickerOption> =
        let icons = project.CustomAssets |> Seq.filter ChecksExport.suitableIcon |> Seq.map (fun a -> { Id = a.Id; Label = (if a.Name = a.Id then a.Id else sprintf "%s (%s)" a.Name a.Id); Missing = false })
        References.pickerEntries (List.ofSeq icons) (Some "(none)") (Option.ofObj current) |> Array.ofList :> IReadOnlyList<PickerOption>

    static member PixelScales: IReadOnlyList<PickerOption> =
        match References.roleOf "ExportSettings" "PixelScale" with
        | Some(FieldRole.OneOf(choices, _)) -> References.choiceOptions choices |> Array.ofList :> IReadOnlyList<PickerOption>
        | _ -> [||] :> IReadOnlyList<PickerOption>

    /// The export problems the project would have with `settings`.
    static member Check(project: GameProject, settings: ExportSettings) : IReadOnlyList<Problem> =
        let sink = Sink()
        ChecksExport.run (Document.run project (SetExportSettings(Some settings))) sink
        sink.ToList() |> Array.ofList :> IReadOnlyList<Problem>

/// Content list actions beyond add/save/delete (ItemEditor, CropEditor, ActionsEditor).
[<AbstractClass; Sealed>]
type ContentActions =
    /// What `Edits.AddToInventory(itemId)` does to `project`, as ItemEditor's toast.
    static member AddToInventoryMessage(project: GameProject, itemId: string) : string =
        let name () = project.Items |> List.tryFind (fun i -> i.Id = itemId) |> Option.map (fun i -> i.Name) |> Option.defaultValue itemId
        match fst (EditContent.inventoryAdd itemId project) with
        | InventoryAddResult.Added -> sprintf "Added %s to inventory" (name ())
        | InventoryAddResult.StackFull -> sprintf "%s stack is full" (name ())
        | InventoryAddResult.InventoryFull -> "Inventory is full!"
        | InventoryAddResult.UnknownItem -> sprintf "Save %s before adding it to the inventory." itemId

/// The Project Settings view's weather odds, mine card and season arrows (`SettingsForms`).
[<AbstractClass; Sealed>]
type SettingsForm =
    /// The weight shown for one season and weather type (0 without an entry).
    static member WeatherWeight(project: GameProject, seasonId: string, weatherId: string) : float =
        SettingsForms.weatherWeight project seasonId weatherId

    /// The weather table as one undo step: (season id, weather id, weight) per cell; only the
    /// changed cells become edits, negative and non-finite weights read as 0 (no entry).
    static member WeatherOdds(project: GameProject, weights: seq<struct (string * string * float)>) : Edit =
        SettingsForms.weatherOdds project (weights |> Seq.map (fun (struct (s, w, v)) -> (s, w, v)) |> List.ofSeq)

    /// The mine config from the card's fields, clamped like the web inputs.
    static member Mine(project: GameProject, enabled: bool, entranceSceneId: string, x: float, y: float, floors: float, ladderChance: float) : MineConfig =
        SettingsForms.mine project enabled entranceSceneId x y floors ladderChance

    static member CanMoveSeason(project: GameProject, seasonId: string, delta: int) : bool =
        SettingsForms.canMoveSeason project seasonId delta

/// What `Patterns.Build` hands to C#: the edit to apply, or the message to show.
[<Sealed>]
type PatternResult internal (edit: Edit option, error: string option) =
    member _.Edit: Edit | null = Option.toObj edit
    member _.Error: string | null = Option.toObj error
    member _.Ok = edit.IsSome

/// `Patterns` for C#.
[<AbstractClass; Sealed>]
type Patterns =
    static member All: IReadOnlyList<PatternInfo> = Patterns.all |> Array.ofList :> IReadOnlyList<PatternInfo>

    static member Build(kind: PatternKind, options: PatternOptions, project: GameProject) : PatternResult =
        match Patterns.build kind options project with
        | Ok edit -> PatternResult(Some edit, None)
        | Error message -> PatternResult(None, Some message)

    static member Options(name: string, text: string, x: int, y: int, day: int, npcId: string, friendship: int, consequences: bool) : PatternOptions =
        { Name = name; Text = text; X = x; Y = y; Day = day; NpcId = npcId; Friendship = friendship; Consequences = consequences }

    static member SuccessMessage(kind: PatternKind, name: string) : string = Patterns.successMessage kind name
