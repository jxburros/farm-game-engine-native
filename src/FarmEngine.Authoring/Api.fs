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
    static member InstallPack(pack: ContentPack) : Edit = InstallPack pack
    static member SetPackEnabled(packId: string, enabled: bool) : Edit = SetPackEnabled(packId, enabled)
    static member ReorderPacks(packIds: seq<string>) : Edit = ReorderPacks(List.ofSeq packIds)
    /// ModsEditor `move`: swap a pack with its neighbour in the load order.
    static member MovePack(project: GameProject, packId: string, delta: int) : Edit =
        let ids = project.ContentPacks |> Seq.map (fun i -> i.Pack.Manifest.Id) |> Array.ofSeq
        match Array.tryFindIndex (fun id -> id = packId) ids with
        | Some index when index + delta >= 0 && index + delta < ids.Length ->
            let swapped = Array.copy ids
            swapped[index] <- ids[index + delta]
            swapped[index + delta] <- ids[index]
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
