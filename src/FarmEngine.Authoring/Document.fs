namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// An open project plus its undo/redo history. Documents are immutable: `apply` returns a new
/// one and structural sharing keeps that cheap. `Stroke` names the drag-paint stroke whose edits
/// are being merged into the newest history entry (see `applyInStroke`).
type Document =
    { Project: GameProject
      Past: GameProject list
      Future: GameProject list
      Stroke: string option }

module Document =
    /// Undo depth (web `UNDO_LIMIT`).
    [<Literal>]
    let UndoLimit = 50

    let create (project: GameProject) =
        { Project = project; Past = []; Future = []; Stroke = None }

    /// The project after `edit`, with no history bookkeeping (used by `apply` and by previews).
    /// A no-op hands back the same `GameProject` instance.
    let rec run (project: GameProject) (edit: Edit) : GameProject =
        match edit with
        | PaintTiles(sceneId, layer, cells, tileType) -> EditScenes.paintTiles sceneId layer cells tileType project
        | EraseLayer(sceneId, layer, cells) -> EditScenes.eraseLayer sceneId layer cells project
        | FillRect(sceneId, layer, x0, y0, x1, y1, tileType) -> EditScenes.fillRect sceneId layer x0 y0 x1 y1 tileType project
        | FloodFill(sceneId, layer, x, y, tileType) -> EditScenes.floodFill sceneId layer x y tileType project
        | PasteTiles(sceneId, x, y, tiles) -> EditScenes.pasteTiles sceneId x y tiles project
        | SetCollision(sceneId, cells, blocked) -> EditScenes.setCollision sceneId cells blocked project
        | SetTileVisual(sceneId, layer, cells, visual) -> EditScenes.setTileVisual sceneId layer cells visual project
        | PlaceNode(sceneId, x, y, nodeTypeId) -> EditScenes.placeNode sceneId x y nodeTypeId project
        | RemoveNode(sceneId, x, y) -> EditScenes.removeNode sceneId x y project
        | PlaceItem(sceneId, x, y, item) -> EditScenes.placeItem sceneId x y item project
        | RemovePlacedItem(sceneId, x, y) -> EditScenes.removePlacedItem sceneId x y project
        | PlaceMachine(sceneId, x, y, machineTypeId) -> EditScenes.placeMachine sceneId x y machineTypeId project
        | RemoveMachine(sceneId, x, y) -> EditScenes.removeMachine sceneId x y project
        | ClearTile(sceneId, x, y) -> EditScenes.clearTile sceneId x y project
        | ClearCropsAndItems sceneId -> EditScenes.clearCropsAndItems sceneId project
        | ResetSoil sceneId -> EditScenes.resetSoil sceneId project
        | FillScene(sceneId, tileType) -> EditScenes.fillScene sceneId tileType project
        | AddScene scene -> EditScenes.addScene scene project
        | RemoveScene sceneId -> EditScenes.removeScene sceneId project
        | RenameScene(sceneId, name) -> EditScenes.renameScene sceneId name project
        | SetSceneIndoor(sceneId, indoor) -> EditScenes.setSceneIndoor sceneId indoor project
        | ResizeScene(sceneId, width, height) -> EditScenes.resizeScene sceneId width height project
        | DuplicateScene(sceneId, newSceneId) -> EditScenes.duplicateScene sceneId newSceneId project
        | SetTransition(sceneId, transition) -> EditScenes.setTransition sceneId transition project
        | RemoveTransition(sceneId, fromX, fromY) -> EditScenes.removeTransition sceneId fromX fromY project
        | ClearTransitions sceneId -> EditScenes.clearTransitions sceneId project
        | SetStartScene sceneId -> EditScenes.setStartScene sceneId project
        | SetPlayerStart(sceneId, x, y) -> EditScenes.setPlayerStart sceneId x y project
        | SelectBrush(tileType, visual) -> EditScenes.selectBrush tileType visual project
        | UpsertNpc npc -> EditContent.upsertNpc npc project
        | RemoveNpc npcId -> EditContent.removeNpc npcId project
        | MoveNpc(npcId, sceneId, x, y) -> EditContent.moveNpc npcId sceneId x y project
        | UpsertDialogue dialogue -> EditContent.upsertDialogue dialogue project
        | RemoveDialogue dialogueId -> EditContent.removeDialogue dialogueId project
        | UpsertItem item -> EditContent.upsertItem item project
        | RemoveItem itemId -> EditContent.removeItem itemId project
        | AddToInventory itemId -> EditContent.addToInventory itemId project
        | UpsertCrop crop -> EditContent.upsertCrop crop project
        | RemoveCrop cropId -> EditContent.removeCrop cropId project
        | UpsertQuest quest -> EditContent.upsertQuest quest project
        | RemoveQuest questId -> EditContent.removeQuest questId project
        | UpsertEvent event -> EditContent.upsertEvent event project
        | RemoveEvent eventId -> EditContent.removeEvent eventId project
        | UpsertShop shop -> EditContent.upsertShop shop project
        | RemoveShop shopId -> EditContent.removeShop shopId project
        | UpsertRecipe recipe -> EditContent.upsertRecipe recipe project
        | RemoveRecipe recipeId -> EditContent.removeRecipe recipeId project
        | UpsertNodeType nodeType -> EditContent.upsertNodeType nodeType project
        | RemoveNodeType nodeTypeId -> EditContent.removeNodeType nodeTypeId project
        | UpsertMachineType machineType -> EditContent.upsertMachineType machineType project
        | RemoveMachineType machineTypeId -> EditContent.removeMachineType machineTypeId project
        | UpsertAnimalSpecies species -> EditContent.upsertAnimalSpecies species project
        | RemoveAnimalSpecies speciesId -> EditContent.removeAnimalSpecies speciesId project
        | UpsertAnimal animal -> EditContent.upsertAnimal animal project
        | RemoveAnimal animalId -> EditContent.removeAnimal animalId project
        | UpsertFishTable table -> EditContent.upsertFishTable table project
        | RemoveFishTable tableId -> EditContent.removeFishTable tableId project
        | UpsertAction action -> EditContent.upsertAction action project
        | RemoveAction actionId -> EditContent.removeAction actionId project
        | UpsertMinigame minigame -> EditContent.upsertMinigame minigame project
        | RemoveMinigame minigameId -> EditContent.removeMinigame minigameId project
        | SetWeather config -> EditContent.setWeather config project
        | SetWeatherWeight(seasonId, weatherId, weight) -> EditContent.setWeatherWeight seasonId weatherId weight project
        | SetMine config -> EditContent.setMine config project
        | SetGamePanels panels -> EditContent.setGamePanels panels project
        | SetProjectInfo(name, version) -> EditProject.setProjectInfo name version project
        | SetSettings settings -> EditProject.setSettings settings project
        | SetExportSettings settings -> EditProject.setExportSettings settings project
        | RemoveSeason seasonId -> EditProject.removeSeason seasonId project
        | MoveSeason(seasonId, delta) -> EditProject.moveSeason seasonId delta project
        | SetGraphics graphics -> EditProject.setGraphics graphics project
        | SetPlayerVisual visual -> EditProject.setPlayerVisual visual project
        | BindVisual(target, visual) -> EditProject.bindVisual target visual project
        | UpsertAsset asset -> EditProject.upsertAsset asset project
        | RemoveAsset assetId -> EditProject.removeAsset assetId project
        | SetFrameTicks(assetId, clip, frame, ticks) -> EditProject.setFrameTicks assetId clip frame ticks project
        | DuplicateFrame(assetId, clip, frame) -> EditProject.duplicateFrame assetId clip frame project
        | InstallPack pack -> EditProject.installPack pack project
        | SetPackEnabled(packId, enabled) -> EditProject.setPackEnabled packId enabled project
        | ReorderPacks ids -> EditProject.reorderPacks ids project
        | RemovePack packId -> EditProject.removePack packId project
        | ImportPack packId -> EditProject.importPack packId project
        | ReplaceProject next -> EditProject.replaceProject next project
        | Batch(_, edits) -> edits |> List.fold run project

    let private push (project: GameProject) (past: GameProject list) =
        let past = project :: past
        if past.Length > UndoLimit then List.truncate UndoLimit past else past

    /// Applies `edit`, recording one undo entry when the project changed (a `Batch` is one entry).
    /// No-ops leave the document untouched (same instance). Ends any drag stroke.
    let apply (edit: Edit) (document: Document) : Document =
        let next = run document.Project edit
        if LanguagePrimitives.PhysicalEquality next document.Project then
            (if document.Stroke.IsNone then document else { document with Stroke = None })
        else
            { Project = next; Past = push document.Project document.Past; Future = []; Stroke = None }

    /// Drag painting (web `beginPaintStroke` / `endPaintStroke`): every edit of one stroke lands in
    /// ONE undo entry. The first changing edit of a stroke pushes history; later edits with the
    /// same `strokeId` replace the project in place. A different id (or a plain `apply`, undo,
    /// redo) starts a new entry, so the stroke needs no explicit end.
    let applyInStroke (strokeId: string) (edit: Edit) (document: Document) : Document =
        let next = run document.Project edit
        if LanguagePrimitives.PhysicalEquality next document.Project then document
        elif document.Stroke = Some strokeId then { document with Project = next }
        else { Project = next; Past = push document.Project document.Past; Future = []; Stroke = Some strokeId }

    /// Ends the current stroke, so the next stroke edit starts a new history entry.
    let endStroke (document: Document) : Document =
        if document.Stroke.IsNone then document else { document with Stroke = None }

    let canUndo (document: Document) = not document.Past.IsEmpty
    let canRedo (document: Document) = not document.Future.IsEmpty

    let undo (document: Document) : Document =
        match document.Past with
        | [] -> document
        | previous :: rest -> { Project = previous; Past = rest; Future = document.Project :: document.Future; Stroke = None }

    let redo (document: Document) : Document =
        match document.Future with
        | [] -> document
        | next :: rest -> { Project = next; Past = document.Project :: document.Past; Future = rest; Stroke = None }
