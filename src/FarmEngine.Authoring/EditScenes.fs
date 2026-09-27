namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Core
open FarmEngine.Schemas

/// The tile and scene edits (web EditorPanel.tsx tile painter, App.tsx `handleTileClick`,
/// SceneManager.tsx, TransitionEditor.tsx). Each function is `GameProject -> GameProject` and
/// returns the same instance when nothing changed.
module internal EditScenes =
    let private setField (record: 'T) (name: string) (value: objnull) : 'T = Records.withValue record name value

    /// The brush art rides along when painting the selected type (web `p.selectedTileVisual`).
    let private brushVisual (project: GameProject) (tileType: string) : VisualRef option =
        if project.SelectedTileType = tileType then Option.ofObj project.SelectedTileVisual else None

    let private paintCells (sceneId: string) (layer: TileLayer) (cells: (int * int) list) (tileType: string) (project: GameProject) =
        let visual = brushVisual project tileType
        let next = Proj.mapScene sceneId (Proj.mapCells cells (TileRules.paint layer tileType visual)) project
        if obj.ReferenceEquals(next, project) then project
        elif next.SelectedTileType = tileType then next
        else Proj.set "SelectedTileType" (box tileType) next

    /// Web single brush over any number of cells (a drag stroke sends one cell at a time).
    let paintTiles sceneId layer cells tileType project = paintCells sceneId layer cells tileType project

    let eraseLayer (sceneId: string) (layer: TileLayer) (cells: (int * int) list) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCells cells (TileRules.erase layer)) project

    /// Web `paintRect`: inclusive corners, clamped to the scene.
    let fillRect (sceneId: string) (layer: TileLayer) x0 y0 x1 y1 (tileType: string) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some scene -> paintCells sceneId layer (TileRules.rectCells scene x0 y0 x1 y1) tileType project

    /// Web `floodFill`: the contiguous region of the clicked tile's type; painting the same type
    /// with no art is a no-op (crops and nodes stay), exactly like the web.
    let floodFill (sceneId: string) (layer: TileLayer) (x: int) (y: int) (tileType: string) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some scene when not (Proj.inBounds scene x y) -> project
        | Some scene when scene.Tiles.[y].[x].Type = tileType && (brushVisual project tileType).IsNone -> project
        | Some scene -> paintCells sceneId layer (TileRules.regionCells scene x y) tileType project

    /// Web `pasteTileRegion`: stamp with the top-left at (x, y); tiles outside the scene are skipped.
    let pasteTiles (sceneId: string) (x: int) (y: int) (region: Tile list list) (project: GameProject) =
        Proj.mapScene sceneId (fun scene ->
            let rows = Proj.rows scene
            let mutable changed = false
            region |> List.iteri (fun dy row ->
                row |> List.iteri (fun dx tile ->
                    let tx, ty = x + dx, y + dy
                    if ty >= 0 && ty < rows.Length && tx >= 0 && tx < rows[ty].Length then
                        let placed = Records.withValues tile [ ("X", box (float tx)); ("Y", box (float ty)) ]
                        if not (obj.Equals(placed, rows[ty][tx])) then
                            rows[ty][tx] <- placed
                            changed <- true))
            Proj.commitRows scene rows changed) project

    let setCollision (sceneId: string) (cells: (int * int) list) (blocked: bool) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCells cells (fun tile -> if tile.Collision = blocked then tile else setField tile "Collision" (box blocked))) project

    let setTileVisual (sceneId: string) (layer: TileLayer) (cells: (int * int) list) (visual: VisualRef option) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCells cells (TileRules.withVisual layer visual)) project

    /// Node types a creator can place: the built-in ones (incl. mine rocks) plus the project's own.
    let placeableNodeTypes (project: GameProject) : NodeTypeDefinition list =
        [ yield! ContentBuiltin.DefaultNodeTypes
          yield! ContentBuiltin.MineNodeTypes
          yield! project.NodeTypes ]

    /// App.tsx node placement mode: `{ typeId, remainingHealth: def.health }`; unknown types are ignored.
    let placeNode (sceneId: string) (x: int) (y: int) (nodeTypeId: string) (project: GameProject) =
        match placeableNodeTypes project |> List.tryFind (fun def -> def.Id = nodeTypeId) with
        | None -> project
        | Some def ->
            let node = TileNode(TypeId = def.Id, RemainingHealth = def.Health)
            Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if obj.Equals(tile.Node, node) then tile else setField tile "Node" (box node))) project

    let removeNode (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if isNull tile.Node then tile else setField tile "Node" null)) project

    /// App.tsx item placement mode: the item definition is dropped on the tile.
    let placeItem (sceneId: string) (x: int) (y: int) (item: Item) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if obj.Equals(tile.Item, item) then tile else setField tile "Item" (box item))) project

    let removePlacedItem (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if isNull tile.Item then tile else setField tile "Item" null)) project

    let placeMachine (sceneId: string) (x: int) (y: int) (machineTypeId: string) (project: GameProject) =
        if not (project.MachineTypes |> Seq.exists (fun m -> m.Id = machineTypeId)) then project
        else
            let machine = TileMachine(TypeId = machineTypeId)
            Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if obj.Equals(tile.Machine, machine) then tile else setField tile "Machine" (box machine))) project

    let removeMachine (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if isNull tile.Machine then tile else setField tile "Machine" null)) project

    /// EditorPanel "Clear Items".
    let clearCropsAndItems (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            if isNull tile.Crop && isNull tile.Item then tile else Records.withValues tile [ ("Crop", null); ("Item", null) ])) project

    /// EditorPanel "Reset Soil": soil tiles become dry grass with no crop.
    let resetSoil (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            if tile.Type <> TileTypes.Soil then tile
            else
                Records.withValues (Tiles.SetTileLayer(tile, TileTypes.Grass))
                    [ ("Crop", null); ("SoilState", box SoilStates.Dry); ("SoilMoisture", box 0.0); ("SoilFertility", box 0.0) ])) project

    /// SceneManager `fillScene`: every tile takes the type (through the layer rules), losing crops and items.
    let fillScene (sceneId: string) (tileType: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            Records.withValues (Tiles.SetTileLayer(tile, tileType)) [ ("Crop", null); ("Item", null) ])) project

    /// SceneManager `createScene` / `duplicateScene` (the scene is built by `Defaults`); an existing id is a no-op.
    let addScene (scene: Scene) (project: GameProject) =
        if project.Scenes |> Seq.exists (fun s -> s.Id = scene.Id) then project
        else Proj.set "Scenes" (box (Lists.append scene project.Scenes)) project

    /// SceneManager `deleteScene`: never the last scene, never the start scene.
    let removeScene (sceneId: string) (project: GameProject) =
        if project.Scenes.Count <= 1 || sceneId = project.StartSceneId then project
        else
            match Lists.removeBy (fun (s: Scene) -> s.Id) sceneId project.Scenes with
            | None -> project
            | Some scenes -> Proj.set "Scenes" (box scenes) project |> Cleanup.dropScene sceneId

    /// SceneManager `saveSceneName`: blank names are refused.
    let renameScene (sceneId: string) (name: string) (project: GameProject) =
        if System.String.IsNullOrWhiteSpace name then project
        else Proj.mapScene sceneId (fun scene -> if scene.Name = name then scene else setField scene "Name" (box name)) project

    /// SceneManager `applyResize`: tiles in the overlap are kept, new tiles are dry grass.
    let resizeScene (sceneId: string) (width: int) (height: int) (project: GameProject) =
        if width < 1 || height < 1 then project
        else
            Proj.mapScene sceneId (fun scene ->
                if int scene.Width = width && int scene.Height = height && scene.Tiles.Count = height
                   && scene.Tiles |> Seq.forall (fun row -> row.Count = width) then scene
                else
                    let tiles = List<List<Tile>>(height)
                    for y in 0 .. height - 1 do
                        let row = List<Tile>(width)
                        for x in 0 .. width - 1 do
                            if Proj.inBounds scene x y then row.Add(scene.Tiles.[y].[x])
                            else row.Add(setField (Tiles.CreateEmptyTile(float x, float y, TileTypes.Grass)) "SoilState" (box SoilStates.Dry))
                        tiles.Add row
                    Records.withValues scene [ ("Width", box (float width)); ("Height", box (float height)); ("Tiles", box tiles) ]) project

    /// SceneManager `duplicateScene`: a copy with a new id, "(Copy)" appended and no NPC list.
    let duplicateScene (sceneId: string) (newSceneId: string) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some _ when project.Scenes |> Seq.exists (fun s -> s.Id = newSceneId) -> project
        | Some scene ->
            let tiles = List<List<Tile>>(scene.Tiles |> Seq.map (fun row -> List<Tile>(row)))
            let copy =
                Records.withValues scene
                    [ ("Id", box newSceneId)
                      ("Name", box (sprintf "%s (Copy)" scene.Name))
                      ("Tiles", box tiles)
                      ("Transitions", box (List<SceneTransition>(scene.Transitions)))
                      ("Npcs", box (List<string>()))
                      ("Events", box (List<string>(scene.Events))) ]
            addScene copy project

    /// TransitionEditor create/update: one transition per departure tile.
    let setTransition (sceneId: string) (transition: SceneTransition) (project: GameProject) =
        Proj.mapScene sceneId (fun scene ->
            let sameTile (t: SceneTransition) = t.FromX = transition.FromX && t.FromY = transition.FromY
            match Seq.tryFindIndex sameTile scene.Transitions with
            | Some index when obj.Equals(scene.Transitions[index], transition) -> scene
            | Some index ->
                let transitions = List<SceneTransition>(scene.Transitions)
                transitions[index] <- transition
                setField scene "Transitions" (box transitions)
            | None -> setField scene "Transitions" (box (Lists.append transition scene.Transitions))) project

    let removeTransition (sceneId: string) (fromX: int) (fromY: int) (project: GameProject) =
        Proj.mapScene sceneId (fun scene ->
            match Lists.filterChanged (fun (t: SceneTransition) -> not (int t.FromX = fromX && int t.FromY = fromY)) scene.Transitions with
            | Some transitions -> setField scene "Transitions" (box transitions)
            | None -> scene) project

    let clearTransitions (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (fun scene -> if scene.Transitions.Count = 0 then scene else setField scene "Transitions" (box (List<SceneTransition>()))) project

    /// SceneManager `setAsStartScene`; unknown scenes are ignored.
    let setStartScene (sceneId: string) (project: GameProject) =
        if project.StartSceneId = sceneId || (Proj.tryScene sceneId project).IsNone then project
        else Proj.set "StartSceneId" (box sceneId) project

    /// SceneManager `switchToScene` and the NPC/player placement: where the player stands when play starts.
    let setPlayerStart (sceneId: string) (x: int) (y: int) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some _ ->
            let player = project.Player
            if player.SceneId = sceneId && player.X = float x && player.Y = float y then project
            else Proj.set "Player" (box (Records.withValues player [ ("SceneId", box sceneId); ("X", box (float x)); ("Y", box (float y)) ])) project

    /// EditorPanel palette click / eyedropper / ArtBindings "tiles": the brush lives in the project.
    let selectBrush (tileType: string) (visual: VisualRef option) (project: GameProject) =
        let visualValue: objnull =
            match visual with
            | Some v -> box v
            | None -> null
        if project.SelectedTileType = tileType && obj.Equals(project.SelectedTileVisual, visualValue) && project.Mode = EditorModes.Tiles then project
        else Proj.setMany [ ("SelectedTileType", box tileType); ("SelectedTileVisual", visualValue); ("Mode", box EditorModes.Tiles) ] project
