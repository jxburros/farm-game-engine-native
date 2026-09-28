namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Change-tracking helpers over the C# `List<T>` collections of the schema records. Every function
/// returns `None` when nothing changed, so edits can hand back the same project instance (the
/// "no-op returns the same document" rule of `Document.apply`).
module internal Lists =
    let ofSeq (items: seq<'T>) = List<'T>(items)

    /// `Some list'` when `f` changed at least one element (by record equality), else `None`.
    let mapChanged (f: 'T -> 'T) (items: List<'T>) : List<'T> option =
        let mutable changed = false
        let next = List<'T>(items.Count)
        for item in items do
            let mapped = f item
            if not (obj.Equals(mapped, item)) then changed <- true
            next.Add mapped
        if changed then Some next else None

    /// Elements kept by `keep`; `None` when all were kept.
    let filterChanged (keep: 'T -> bool) (items: List<'T>) : List<'T> option =
        let next = List<'T>(items |> Seq.filter keep)
        if next.Count = items.Count then None else Some next

    /// Replace the element with the same id, or append. `None` when the existing element is equal.
    let upsertBy (idOf: 'T -> string) (item: 'T) (items: List<'T>) : List<'T> option =
        let id = idOf item
        match Seq.tryFindIndex (fun existing -> idOf existing = id) items with
        | Some index when obj.Equals(items[index], item) -> None
        | Some index ->
            let next = List<'T>(items)
            next[index] <- item
            Some next
        | None ->
            let next = List<'T>(items)
            next.Add item
            Some next

    let removeBy (idOf: 'T -> string) (id: string) (items: List<'T>) : List<'T> option =
        filterChanged (fun item -> idOf item <> id) items

    let append (item: 'T) (items: List<'T>) : List<'T> =
        let next = List<'T>(items)
        next.Add item
        next

/// Copy-with over `GameProject` and `Scene` (see `Records`): one place knows the property names.
module internal Proj =
    let set (name: string) (value: objnull) (project: GameProject) : GameProject =
        Records.withValue project name value

    let setMany (changes: (string * objnull) list) (project: GameProject) : GameProject =
        Records.withValues project changes

    /// Apply `Some list'` from a `Lists` helper to a project field; `None` leaves the instance alone.
    let update (name: string) (next: List<'T> option) (project: GameProject) : GameProject =
        match next with
        | Some list -> set name (box list) project
        | None -> project

    let sceneIndex (sceneId: string) (project: GameProject) =
        project.Scenes.FindIndex(fun s -> s.Id = sceneId)

    let tryScene (sceneId: string) (project: GameProject) : Scene option =
        match sceneIndex sceneId project with
        | -1 -> None
        | index -> Some project.Scenes[index]

    let startScene (project: GameProject) : Scene option =
        match tryScene project.StartSceneId project with
        | Some scene -> Some scene
        | None -> if project.Scenes.Count = 0 then None else Some project.Scenes[0]

    /// The scene the editor works in (web `project.player.sceneId`), falling back to the start scene.
    let currentScene (project: GameProject) : Scene option =
        match tryScene project.Player.SceneId project with
        | Some scene -> Some scene
        | None -> startScene project

    /// Replace one scene (by id) with `f scene`; the same project when `f` returns an equal scene.
    let mapScene (sceneId: string) (f: Scene -> Scene) (project: GameProject) : GameProject =
        match sceneIndex sceneId project with
        | -1 -> project
        | index ->
            let scene = project.Scenes[index]
            let next = f scene
            if obj.Equals(next, scene) then
                project
            else
                let scenes = List<Scene>(project.Scenes)
                scenes[index] <- next
                set "Scenes" (box scenes) project

    /// Every scene through `f`; the same project when none changed.
    let mapScenes (f: Scene -> Scene) (project: GameProject) : GameProject =
        update "Scenes" (Lists.mapChanged f project.Scenes) project

    let withSceneTiles (tiles: List<List<Tile>>) (scene: Scene) : Scene = Records.withValue scene "Tiles" (box tiles)

    let inBounds (scene: Scene) (x: int) (y: int) =
        y >= 0 && y < scene.Tiles.Count && x >= 0 && x < scene.Tiles[y].Count

    /// Tiles as a mutable row array for a bulk edit; `commit` turns it back into a scene when changed.
    let rows (scene: Scene) : Tile[][] = scene.Tiles |> Seq.map Seq.toArray |> Seq.toArray

    let commitRows (scene: Scene) (rows: Tile[][]) (changed: bool) : Scene =
        if not changed then scene
        else withSceneTiles (List<List<Tile>>(rows |> Array.map (fun r -> List<Tile>(r)))) scene

    /// Map every tile of a scene; the same scene when nothing changed.
    let mapTiles (f: Tile -> Tile) (scene: Scene) : Scene =
        let rows = rows scene
        let mutable changed = false
        for y in 0 .. rows.Length - 1 do
            for x in 0 .. rows[y].Length - 1 do
                let next = f (rows[y][x])
                if not (obj.Equals(next, rows[y][x])) then
                    rows[y][x] <- next
                    changed <- true
        commitRows scene rows changed

    /// Map the tiles at `cells` (out-of-bounds cells are ignored); the same scene when nothing changed.
    let mapCells (cells: (int * int) list) (f: Tile -> Tile) (scene: Scene) : Scene =
        let rows = rows scene
        let mutable changed = false
        for (x, y) in cells do
            if y >= 0 && y < rows.Length && x >= 0 && x < rows[y].Length then
                let next = f (rows[y][x])
                if not (obj.Equals(next, rows[y][x])) then
                    rows[y][x] <- next
                    changed <- true
        commitRows scene rows changed

    let mapCell (x: int) (y: int) (f: Tile -> Tile) (scene: Scene) : Scene = mapCells [ (x, y) ] f scene

    let allIds (project: GameProject) : HashSet<string> =
        let ids = HashSet<string>()
        let add (xs: seq<string>) = for x in xs do ids.Add x |> ignore
        add (project.Scenes |> Seq.map (fun s -> s.Id))
        add (project.Npcs |> Seq.map (fun n -> n.Id))
        add (project.Items |> Seq.map (fun i -> i.Id))
        add (project.Events |> Seq.map (fun e -> e.Id))
        add (project.Dialogues |> Seq.map (fun d -> d.Id))
        add (project.Quests |> Seq.map (fun q -> q.Id))
        add (project.Shops |> Seq.map (fun s -> s.Id))
        add (project.NodeTypes |> Seq.map (fun n -> n.Id))
        add (project.Recipes |> Seq.map (fun r -> r.Id))
        add (project.MachineTypes |> Seq.map (fun m -> m.Id))
        add (project.AnimalSpecies |> Seq.map (fun a -> a.Id))
        add (project.Animals |> Seq.map (fun a -> a.Id))
        add (project.FishTables |> Seq.map (fun f -> f.Id))
        add (project.Actions |> Seq.map (fun a -> a.Id))
        add (project.Minigames |> Seq.map (fun m -> m.Id))
        add (project.CustomAssets |> Seq.map (fun a -> a.Id))
        match project.CustomCrops with
        | null -> ()
        | crops -> add (crops |> Seq.map (fun c -> c.Id))
        ids

/// Tile-layer rules shared by the paint edits (web `game-helpers.ts` `setTileLayer`, `classifyTileType`).
module TileRules =
    /// The layer a tile type naturally lives on (web `classifyTileType`).
    let layerOf (tileType: string) : TileLayer =
        match AuthoringTiles.ClassifyTileType tileType with
        | "overlay" -> Overlay
        | "object" -> Object
        | _ -> Background

    let private optionalString (value: string | null) : string option =
        match value with
        | null -> None
        | s -> Some s

    /// The per-layer art override on a tile, if any.
    let visualOf (layer: TileLayer) (tile: Tile) : VisualRef option =
        match tile.Visuals with
        | null -> None
        | visuals ->
            match layer with
            | Background -> Option.ofObj visuals.Background
            | Overlay -> Option.ofObj visuals.Overlay
            | Object -> Option.ofObj visuals.Object

    /// Set one layer's art override (`None` clears it); `Visuals` becomes null when all three are empty.
    let withVisual (layer: TileLayer) (visual: VisualRef option) (tile: Tile) : Tile =
        let current =
            match tile.Visuals with
            | null -> TileVisuals()
            | visuals -> visuals
        let value: objnull =
            match visual with
            | Some v -> box v
            | None -> null
        let next =
            match layer with
            | Background -> Records.withValue current "Background" value
            | Overlay -> Records.withValue current "Overlay" value
            | Object -> Records.withValue current "Object" value
        let empty = isNull next.Background && isNull next.Overlay && isNull next.Object
        let visuals: objnull = if empty then null else box next
        if obj.Equals(visuals, tile.Visuals) then tile else Records.withValue tile "Visuals" visuals

    /// Paint `tileType` onto `layer`. On its natural layer this is exactly the web brush
    /// (`setTileLayer` + no crop, no node). On another layer the type is written there and still
    /// becomes the tile's effective `Type`, so the eyedropper picks up what was painted last.
    let paint (layer: TileLayer) (tileType: string) (visual: VisualRef option) (tile: Tile) : Tile =
        let painted =
            if layerOf tileType = layer then
                AuthoringTiles.SetTileLayer(tile, tileType, Option.toObj visual)
            else
                let byLayer =
                    match layer with
                    | Background -> Records.withValues tile [ ("Type", box tileType); ("Background", box tileType) ]
                    | Overlay -> Records.withValues tile [ ("Type", box tileType); ("Overlay", box tileType) ]
                    | Object -> Records.withValues tile [ ("Type", box tileType); ("Object", box tileType); ("Collision", box (tileType = TileTypes.Wall)) ]
                withVisual layer visual byLayer
        Records.withValues painted [ ("Crop", null); ("Node", null) ]

    /// The effective type once a layer is cleared: topmost remaining layer.
    let private effectiveType (tile: Tile) =
        match tile.Object with
        | null ->
            match tile.Overlay with
            | null -> tile.Background
            | overlay -> overlay
        | object -> object

    /// Clear a layer: background resets to grass, overlay and object to nothing (walls stop blocking).
    let erase (layer: TileLayer) (tile: Tile) : Tile =
        let cleared =
            match layer with
            | Background -> Records.withValue tile "Background" (box TileTypes.Grass)
            | Overlay -> Records.withValue tile "Overlay" null
            | Object -> Records.withValues tile [ ("Object", null); ("Collision", box false) ]
        let cleared = withVisual layer None cleared
        let effective = effectiveType cleared
        if cleared.Type = effective then cleared else Records.withValue cleared "Type" (box effective)

    /// Cells of the inclusive rectangle (x0,y0)-(x1,y1) clamped to the scene, row-major.
    let rectCells (scene: Scene) (x0: int) (y0: int) (x1: int) (y1: int) : (int * int) list =
        let height = scene.Tiles.Count
        let width = if height = 0 then 0 else scene.Tiles[0].Count
        let minX = max 0 (min x0 x1)
        let maxX = min (width - 1) (max x0 x1)
        let minY = max 0 (min y0 y1)
        let maxY = min (height - 1) (max y0 y1)
        [ for y in minY .. maxY do
              for x in minX .. maxX do
                  yield (x, y) ]

    /// The 4-connected region of tiles sharing the start tile's `Type` (web `floodFill`).
    let regionCells (scene: Scene) (startX: int) (startY: int) : (int * int) list =
        if not (Proj.inBounds scene startX startY) then
            []
        else
            let sourceType = scene.Tiles.[startY].[startX].Type
            let seen = HashSet<int * int>()
            let queue = Queue<int * int>()
            seen.Add((startX, startY)) |> ignore
            queue.Enqueue((startX, startY))
            let result = ResizeArray<int * int>()
            while queue.Count > 0 do
                let (x, y) = queue.Dequeue()
                result.Add((x, y))
                for (dx, dy) in [ (0, -1); (0, 1); (-1, 0); (1, 0) ] do
                    let nx, ny = x + dx, y + dy
                    if Proj.inBounds scene nx ny && not (seen.Contains((nx, ny))) && scene.Tiles.[ny].[nx].Type = sourceType then
                        seen.Add((nx, ny)) |> ignore
                        queue.Enqueue((nx, ny))
            List.ofSeq result
