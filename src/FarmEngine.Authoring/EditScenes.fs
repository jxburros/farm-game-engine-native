namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// The tile and scene edits (web EditorPanel.tsx tile painter, App.tsx `handleTileClick`,
/// SceneManager.tsx, TransitionEditor.tsx). Each function is `GameProject -> GameProject` and
/// returns the same instance when nothing changed.
module internal EditScenes =
    /// The brush art rides along when painting the selected type (web `p.selectedTileVisual`).
    let private brushVisual (project: GameProject) (tileType: string) : VisualRef option =
        if project.SelectedTileType = tileType then project.SelectedTileVisual else None

    let private paintCells (sceneId: string) (layer: TileLayer) (cells: (int * int) list) (tileType: string) (project: GameProject) =
        let visual = brushVisual project tileType
        let next = Proj.mapScene sceneId (Proj.mapCells cells (TileRules.paint layer tileType visual)) project
        if LanguagePrimitives.PhysicalEquality next project then project
        elif next.SelectedTileType = tileType then next
        else { next with SelectedTileType = tileType }

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
        | Some scene when (Proj.tryTile scene x y |> Option.exists (fun t -> t.Type = tileType)) && (brushVisual project tileType).IsNone -> project
        | Some scene -> paintCells sceneId layer (TileRules.regionCells scene x y) tileType project

    /// Web `pasteTileRegion`: stamp with the top-left at (x, y); tiles outside the scene are skipped.
    let pasteTiles (sceneId: string) (x: int) (y: int) (region: Tile list list) (project: GameProject) =
        let updates =
            region
            |> Seq.mapi (fun dy row ->
                row |> Seq.mapi (fun dx tile ->
                    let tx, ty = x + dx, y + dy
                    let placed = { tile with X = float tx; Y = float ty }
                    (tx, ty, fun (current: Tile) -> if placed = current then current else placed)))
            |> Seq.concat
        Proj.mapScene sceneId (Proj.updateCells updates) project

    let setCollision (sceneId: string) (cells: (int * int) list) (blocked: bool) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCells cells (fun tile -> if tile.Collision = blocked then tile else { tile with Collision = blocked })) project

    let setTileVisual (sceneId: string) (layer: TileLayer) (cells: (int * int) list) (visual: VisualRef option) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCells cells (TileRules.withVisual layer visual)) project

    /// Node types a creator can place: the built-in ones (incl. mine rocks) plus the project's own.
    let placeableNodeTypes (project: GameProject) : NodeTypeDefinition list =
        [ yield! Builtin.nodeTypes ()
          yield! Builtin.mineNodeTypes ()
          yield! project.NodeTypes ]

    /// App.tsx node placement mode: `{ typeId, remainingHealth: def.health }`; unknown types are ignored.
    let placeNode (sceneId: string) (x: int) (y: int) (nodeTypeId: string) (project: GameProject) =
        match placeableNodeTypes project |> List.tryFind (fun def -> def.Id = nodeTypeId) with
        | None -> project
        | Some def ->
            let node : TileNode = { TypeId = def.Id; RemainingHealth = def.Health; DepletedOnDay = None; Extra = [] }
            Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Node = Some node then tile else { tile with Node = Some node })) project

    let removeNode (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Node.IsNone then tile else { tile with Node = None })) project

    /// App.tsx item placement mode: the item definition is dropped on the tile.
    let placeItem (sceneId: string) (x: int) (y: int) (item: Item) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Item = Some item then tile else { tile with Item = Some item })) project

    let removePlacedItem (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Item.IsNone then tile else { tile with Item = None })) project

    let placeMachine (sceneId: string) (x: int) (y: int) (machineTypeId: string) (project: GameProject) =
        if not (project.MachineTypes |> List.exists (fun m -> m.Id = machineTypeId)) then project
        else
            let machine = { TileMachine.Default with TypeId = machineTypeId }
            Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Machine = Some machine then tile else { tile with Machine = Some machine })) project

    let removeMachine (sceneId: string) (x: int) (y: int) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapCell x y (fun tile -> if tile.Machine.IsNone then tile else { tile with Machine = None })) project

    /// The Remove tool: what was placed on the tile goes, and so does every animal standing on it
    /// (animals stand on the tile their position rounds down to). NPCs stay.
    let clearTile (sceneId: string) (x: int) (y: int) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | Some scene when Proj.inBounds scene x y ->
            let cleared =
                Proj.mapScene sceneId (Proj.mapCell x y (fun tile ->
                    if tile.Crop.IsNone && tile.Node.IsNone && tile.Item.IsNone && tile.Machine.IsNone then tile
                    else { tile with Crop = None; Node = None; Item = None; Machine = None })) project
            let standsHere (a: AnimalState) = a.SceneId = sceneId && int (floor a.X) = x && int (floor a.Y) = y
            match Lists.filterChanged (standsHere >> not) cleared.Animals with
            | Some animals -> { cleared with Animals = animals }
            | None -> cleared
        | _ -> project

    /// Web eyedropper (`case 'eyedropper'`): the tile's type and the art on that type's own layer.
    let brushAt (project: GameProject) (sceneId: string) (x: int) (y: int) : (string * VisualRef option) option =
        match Proj.tryScene sceneId project |> Option.bind (fun scene -> Proj.tryTile scene x y) with
        | Some tile -> Some(tile.Type, TileRules.visualOf (TileRules.layerOf tile.Type) tile)
        | None -> None

    /// EditorPanel "Clear Items".
    let clearCropsAndItems (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            if tile.Crop.IsNone && tile.Item.IsNone then tile else { tile with Crop = None; Item = None })) project

    /// EditorPanel "Reset Soil": soil tiles become dry grass with no crop.
    let resetSoil (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            if tile.Type <> TileTypes.Soil then tile
            else
                { AuthoringTiles.SetTileLayer(tile, TileTypes.Grass) with
                    Crop = None; SoilState = Some SoilStates.Dry; SoilMoisture = 0.0; SoilFertility = 0.0 })) project

    /// SceneManager `fillScene`: every tile takes the type (through the layer rules), losing crops and items.
    let fillScene (sceneId: string) (tileType: string) (project: GameProject) =
        Proj.mapScene sceneId (Proj.mapTiles (fun tile ->
            { AuthoringTiles.SetTileLayer(tile, tileType) with Crop = None; Item = None })) project

    /// SceneManager `createScene` / `duplicateScene` (the scene is built by `Defaults`); an existing id is a no-op.
    let addScene (scene: Scene) (project: GameProject) =
        if project.Scenes |> List.exists (fun s -> s.Id = scene.Id) then project
        else { project with Scenes = Lists.append scene project.Scenes }

    /// SceneManager `deleteScene`: never the last scene, never the start scene.
    let removeScene (sceneId: string) (project: GameProject) =
        if project.Scenes.Length <= 1 || sceneId = project.StartSceneId then project
        else
            match Lists.removeBy (fun (s: Scene) -> s.Id) sceneId project.Scenes with
            | None -> project
            | Some scenes -> { project with Scenes = scenes } |> Cleanup.dropScene sceneId

    /// SceneManager `saveSceneName`: blank names are refused.
    let renameScene (sceneId: string) (name: string) (project: GameProject) =
        if System.String.IsNullOrWhiteSpace name then project
        else Proj.mapScene sceneId (fun scene -> if scene.Name = name then scene else { scene with Name = name }) project

    /// Marks the scene indoor (`indoor: true`) or outdoor (the key absent).
    let setSceneIndoor (sceneId: string) (indoor: bool) (project: GameProject) =
        let value = if indoor then Some true else None
        Proj.mapScene sceneId (fun scene -> if scene.Indoor = value then scene else { scene with Indoor = value }) project

    /// The largest scene side (the game cuts bigger scenes when it loads them).
    [<Literal>]
    let MaxSceneSize = 256

    /// SceneManager `applyResize`: tiles in the overlap are kept, new tiles are dry grass. Rows
    /// that keep their width are shared with the old scene. A shrink moves what stood on the cut
    /// tiles inside (`Cleanup.clampToScene`, #44). Sizes outside 1–256 are ignored.
    let resizeScene (sceneId: string) (width: int) (height: int) (project: GameProject) =
        if width < 1 || height < 1 || width > MaxSceneSize || height > MaxSceneSize then project
        else
            let newTile x y = { AuthoringTiles.CreateEmptyTile(float x, float y, TileTypes.Grass) with SoilState = Some SoilStates.Dry }
            let resized =
                Proj.mapScene sceneId (fun scene ->
                    if int scene.Width = width && int scene.Height = height && scene.Tiles.Length = height
                       && scene.Tiles |> List.forall (fun row -> row.Length = width) then scene
                    else
                        let fit (y: int) (row: Tile list) =
                            if row.Length = width then row
                            elif row.Length > width then List.truncate width row
                            else row @ [ for x in row.Length .. width - 1 -> newTile x y ]
                        let kept = scene.Tiles |> List.truncate height |> List.mapi fit
                        let added = [ for y in kept.Length .. height - 1 -> [ for x in 0 .. width - 1 -> newTile x y ] ]
                        { scene with Width = float width; Height = float height; Tiles = kept @ added }) project
            if LanguagePrimitives.PhysicalEquality resized project then project
            else Cleanup.clampToScene sceneId width height resized

    /// What a resize to `width` × `height` would move or remove (the size note lists it before
    /// the creator applies it): one line per thing standing on, or pointing at, a cut tile.
    let resizeImpact (sceneId: string) (width: int) (height: int) (project: GameProject) : string list =
        if width < 1 || height < 1 then []
        else
            let after = Cleanup.clampToScene sceneId width height project
            let moved (before: 'T list) (next: 'T list) (describe: 'T -> string) =
                if LanguagePrimitives.PhysicalEquality before next then []
                else List.zip before next |> List.filter (fun (a, b) -> not (LanguagePrimitives.PhysicalEquality a b)) |> List.map (fst >> describe)
            let cutDoors =
                match Proj.tryScene sceneId project with
                | Some scene ->
                    scene.Transitions
                    |> List.filter (fun t -> t.FromX >= float width || t.FromY >= float height)
                    |> List.map (fun t -> sprintf "the door at (%g, %g) is removed" t.FromX t.FromY)
                | None -> []
            let landingDoors =
                project.Scenes
                |> List.collect (fun scene ->
                    scene.Transitions
                    |> List.filter (fun t -> t.ToSceneId = sceneId && (t.ToX >= float width || t.ToY >= float height))
                    |> List.map (fun t -> sprintf "the door from \"%s\" (%g, %g) lands nearer" scene.Name t.FromX t.FromY))
            [ if not (LanguagePrimitives.PhysicalEquality project.Player after.Player) then yield "the player start moves inside"
              yield! moved project.Npcs after.Npcs (fun n -> sprintf "NPC \"%s\" moves inside" n.Name)
              yield! moved project.Animals after.Animals (fun a -> sprintf "animal \"%s\" moves inside" a.Name)
              yield! cutDoors
              yield! landingDoors
              yield! moved project.Events after.Events (fun e -> sprintf "event \"%s\" gets its tiles moved inside" e.Name)
              yield! moved project.Actions after.Actions (fun a -> sprintf "action \"%s\" gets its tiles moved inside" a.Name)
              yield! moved project.Minigames after.Minigames (fun m -> sprintf "minigame \"%s\" gets its tiles moved inside" m.Name)
              if not (LanguagePrimitives.PhysicalEquality project.Mine after.Mine) then yield "the mine entrance moves inside" ]

    /// SceneManager `duplicateScene`: a copy with a new id, "(Copy)" appended and no NPC list.
    let duplicateScene (sceneId: string) (newSceneId: string) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some _ when project.Scenes |> List.exists (fun s -> s.Id = newSceneId) -> project
        | Some scene -> addScene { scene with Id = newSceneId; Name = sprintf "%s (Copy)" scene.Name; Npcs = [] } project

    /// TransitionEditor create/update: one transition per departure tile.
    let setTransition (sceneId: string) (transition: SceneTransition) (project: GameProject) =
        Proj.mapScene sceneId (fun scene ->
            let sameTile (t: SceneTransition) = t.FromX = transition.FromX && t.FromY = transition.FromY
            match List.tryFindIndex sameTile scene.Transitions with
            | Some index when scene.Transitions.[index] = transition -> scene
            | Some index -> { scene with Transitions = List.updateAt index transition scene.Transitions }
            | None -> { scene with Transitions = Lists.append transition scene.Transitions }) project

    let removeTransition (sceneId: string) (fromX: int) (fromY: int) (project: GameProject) =
        Proj.mapScene sceneId (fun scene ->
            match Lists.filterChanged (fun (t: SceneTransition) -> not (int t.FromX = fromX && int t.FromY = fromY)) scene.Transitions with
            | Some transitions -> { scene with Transitions = transitions }
            | None -> scene) project

    let clearTransitions (sceneId: string) (project: GameProject) =
        Proj.mapScene sceneId (fun scene -> if List.isEmpty scene.Transitions then scene else { scene with Transitions = [] }) project

    /// SceneManager `setAsStartScene`; unknown scenes are ignored.
    let setStartScene (sceneId: string) (project: GameProject) =
        if project.StartSceneId = sceneId || (Proj.tryScene sceneId project).IsNone then project
        else { project with StartSceneId = sceneId }

    /// SceneManager `switchToScene` and the NPC/player placement: where the player stands when play starts.
    let setPlayerStart (sceneId: string) (x: int) (y: int) (project: GameProject) =
        match Proj.tryScene sceneId project with
        | None -> project
        | Some _ ->
            let player = project.Player
            if player.SceneId = sceneId && player.X = float x && player.Y = float y then project
            else { project with Player = { player with SceneId = sceneId; X = float x; Y = float y } }

    /// EditorPanel palette click / eyedropper / ArtBindings "tiles": the brush lives in the project.
    let selectBrush (tileType: string) (visual: VisualRef option) (project: GameProject) =
        if project.SelectedTileType = tileType && project.SelectedTileVisual = visual && project.Mode = EditorModes.Tiles then project
        else { project with SelectedTileType = tileType; SelectedTileVisual = visual; Mode = EditorModes.Tiles }
