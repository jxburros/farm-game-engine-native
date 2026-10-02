namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Change-tracking helpers over the list fields of the schema records. Every function returns
/// `None` when nothing changed, so edits can hand back the same project instance (the "no-op
/// returns the same document" rule of `Document.apply`).
module internal Lists =
    /// A changed value: a different instance that is not structurally equal (functions that change
    /// nothing hand back the same instance, so the structural check only runs on real changes).
    let changed (next: 'T) (previous: 'T) : bool =
        not (LanguagePrimitives.PhysicalEquality (box next) (box previous)) && not (next = previous)

    /// `Some list'` when `f` changed at least one element, else `None`. `f` runs once per
    /// element and nothing is allocated until the first change; elements `f` hands back as the
    /// same instance stay shared, so mapping the rows of a big scene keeps every untouched row.
    let mapChanged (f: 'T -> 'T) (items: 'T list) : 'T list option =
        let rec scan (index: int) (rest: 'T list) =
            match rest with
            | [] -> None
            | item :: tail ->
                let mapped = f item
                if changed mapped item then Some(index, mapped, tail) else scan (index + 1) tail
        match scan 0 items with
        | None -> None
        | Some(index, mapped, tail) -> Some(List.take index items @ (mapped :: List.map f tail))

    /// Elements kept by `keep`; `None` when all were kept.
    let filterChanged (keep: 'T -> bool) (items: 'T list) : 'T list option =
        let next = List.filter keep items
        if next.Length = items.Length then None else Some next

    /// Replace the element with the same id, or append. `None` when the existing element is equal.
    let upsertBy (idOf: 'T -> string) (item: 'T) (items: 'T list) : 'T list option =
        let id = idOf item
        match List.tryFindIndex (fun existing -> idOf existing = id) items with
        | Some index when items.[index] = item -> None
        | Some index -> Some(List.updateAt index item items)
        | None -> Some(items @ [ item ])

    let removeBy (idOf: 'T -> string) (id: string) (items: 'T list) : 'T list option =
        filterChanged (fun item -> idOf item <> id) items

    let append (item: 'T) (items: 'T list) : 'T list = items @ [ item ]

    /// `list'` from a helper, or the list itself.
    let orSame (items: 'T list) (next: 'T list option) : 'T list = defaultArg next items

/// Scene lookups and whole-scene, whole-tile updates on `GameProject`.
module internal Proj =
    let sceneIndex (sceneId: string) (project: GameProject) =
        project.Scenes |> List.tryFindIndex (fun s -> s.Id = sceneId) |> Option.defaultValue -1

    let tryScene (sceneId: string) (project: GameProject) : Scene option =
        project.Scenes |> List.tryFind (fun s -> s.Id = sceneId)

    let startScene (project: GameProject) : Scene option =
        match tryScene project.StartSceneId project with
        | Some scene -> Some scene
        | None -> List.tryHead project.Scenes

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
            let scene = project.Scenes.[index]
            let next = f scene
            if Lists.changed next scene then { project with Scenes = List.updateAt index next project.Scenes }
            else project

    /// Every scene through `f`; the same project when none changed.
    let mapScenes (f: Scene -> Scene) (project: GameProject) : GameProject =
        match Lists.mapChanged f project.Scenes with
        | Some scenes -> { project with Scenes = scenes }
        | None -> project

    let withSceneTiles (tiles: Tile list list) (scene: Scene) : Scene = { scene with Tiles = tiles }

    /// A copy of the tiles as arrays, for reads that need random access over the whole scene
    /// (flood fill). Edits go through `mapTiles` / `updateCells`, which keep untouched rows.
    let rows (scene: Scene) : Tile[][] = scene.Tiles |> List.map Array.ofList |> Array.ofList

    let inBounds (scene: Scene) (x: int) (y: int) =
        y >= 0 && y < scene.Tiles.Length && x >= 0 && x < scene.Tiles.[y].Length

    /// The tile at (x, y), without copying the scene.
    let tryTile (scene: Scene) (x: int) (y: int) : Tile option =
        if y < 0 || x < 0 then None
        else
            match List.tryItem y scene.Tiles with
            | Some row -> List.tryItem x row
            | None -> None

    /// Map every tile of a scene; the same scene when nothing changed. Rows without a change
    /// stay the same list instances, so undo history shares them (#102).
    let mapTiles (f: Tile -> Tile) (scene: Scene) : Scene =
        match Lists.mapChanged (fun row -> Lists.mapChanged f row |> Lists.orSame row) scene.Tiles with
        | Some tiles -> withSceneTiles tiles scene
        | None -> scene

    /// Apply each `(x, y, f)` in order (out-of-bounds cells are ignored); the same scene when
    /// nothing changed. Only the rows with a changed tile are rebuilt: a one-tile edit of a
    /// 256×256 scene costs one row and the row spine, not 65k cells (#102).
    let updateCells (updates: seq<int * int * (Tile -> Tile)>) (scene: Scene) : Scene =
        // The row lists by index, taken on the first update (an empty update list costs nothing).
        let mutable lines : Tile list[] = [||]
        let mutable taken = false
        let touched = Dictionary<int, Tile[]>()
        let changedRows = HashSet<int>()
        for (x, y, f) in updates do
            if y >= 0 && x >= 0 then
                if not taken then
                    lines <- Array.ofList scene.Tiles
                    taken <- true
                if y < lines.Length then
                    let row =
                        match touched.TryGetValue y with
                        | true, row -> row
                        | _ ->
                            let row = Array.ofList lines.[y]
                            touched.[y] <- row
                            row
                    if x < row.Length then
                        let next = f row.[x]
                        if Lists.changed next row.[x] then
                            row.[x] <- next
                            changedRows.Add y |> ignore
        if changedRows.Count = 0 then scene
        else
            let tiles = lines |> Array.mapi (fun y row -> if changedRows.Contains y then List.ofArray touched.[y] else row)
            withSceneTiles (List.ofArray tiles) scene

    /// Map the tiles at `cells` (out-of-bounds cells are ignored); the same scene when nothing changed.
    let mapCells (cells: (int * int) list) (f: Tile -> Tile) (scene: Scene) : Scene =
        updateCells (cells |> Seq.map (fun (x, y) -> (x, y, f))) scene

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
        | None -> ()
        | Some crops -> add (crops |> Seq.map (fun c -> c.Id))
        ids

/// Tile-layer rules shared by the paint edits (web `game-helpers.ts` `setTileLayer`, `classifyTileType`).
module TileRules =
    /// The layer a tile type naturally lives on (web `classifyTileType`).
    let layerOf (tileType: string) : TileLayer =
        match AuthoringTiles.ClassifyTileType tileType with
        | "overlay" -> Overlay
        | "object" -> Object
        | _ -> Background

    /// The per-layer art override on a tile, if any.
    let visualOf (layer: TileLayer) (tile: Tile) : VisualRef option =
        match tile.Visuals with
        | None -> None
        | Some visuals ->
            match layer with
            | Background -> visuals.Background
            | Overlay -> visuals.Overlay
            | Object -> visuals.Object

    /// Set one layer's art override (`None` clears it); `Visuals` becomes absent when all three are empty.
    let withVisual (layer: TileLayer) (visual: VisualRef option) (tile: Tile) : Tile =
        let current = defaultArg tile.Visuals TileVisuals.Default
        let next =
            match layer with
            | Background -> { current with Background = visual }
            | Overlay -> { current with Overlay = visual }
            | Object -> { current with Object = visual }
        let visuals = if next.Background.IsNone && next.Overlay.IsNone && next.Object.IsNone then None else Some next
        if visuals = tile.Visuals then tile else { tile with Visuals = visuals }

    /// Paint `tileType` onto `layer`. On its natural layer this is exactly the web brush
    /// (`setTileLayer` + no crop, no node). On another layer the type is written there and still
    /// becomes the tile's effective `Type`, so the eyedropper picks up what was painted last.
    let paint (layer: TileLayer) (tileType: string) (visual: VisualRef option) (tile: Tile) : Tile =
        let painted =
            if layerOf tileType = layer then
                AuthoringTiles.SetTileLayer(tile, tileType, visual)
            else
                let byLayer =
                    match layer with
                    | Background -> { tile with Type = tileType; Background = tileType }
                    | Overlay -> { tile with Type = tileType; Overlay = Some tileType }
                    | Object -> { tile with Type = tileType; Object = Some tileType; Collision = (tileType = TileTypes.Wall) }
                withVisual layer visual byLayer
        { painted with Crop = None; Node = None }

    /// The effective type once a layer is cleared: topmost remaining layer.
    let private effectiveType (tile: Tile) =
        match tile.Object with
        | Some object -> object
        | None ->
            match tile.Overlay with
            | Some overlay -> overlay
            | None -> tile.Background

    /// Clear a layer: background resets to grass, overlay and object to nothing (walls stop blocking).
    let erase (layer: TileLayer) (tile: Tile) : Tile =
        let cleared =
            match layer with
            | Background -> { tile with Background = TileTypes.Grass }
            | Overlay -> { tile with Overlay = None }
            | Object -> { tile with Object = None; Collision = false }
        let cleared = withVisual layer None cleared
        let effective = effectiveType cleared
        if cleared.Type = effective then cleared else { cleared with Type = effective }

    /// Cells of the inclusive rectangle (x0,y0)-(x1,y1) clamped to the scene, row-major. The
    /// width is the widest row's, so a ragged grid (whose rows `Proj.mapCells` checks one by one)
    /// is painted wherever it has tiles, not only as wide as its first row.
    let rectCells (scene: Scene) (x0: int) (y0: int) (x1: int) (y1: int) : (int * int) list =
        let height = scene.Tiles.Length
        let width = scene.Tiles |> List.fold (fun widest row -> max widest row.Length) 0
        let minX = max 0 (min x0 x1)
        let maxX = min (width - 1) (max x0 x1)
        let minY = max 0 (min y0 y1)
        let maxY = min (height - 1) (max y0 y1)
        [ for y in minY .. maxY do
              for x in minX .. maxX do
                  yield (x, y) ]

    /// The 4-connected region of tiles sharing the start tile's `Type` (web `floodFill`).
    let regionCells (scene: Scene) (startX: int) (startY: int) : (int * int) list =
        let tiles = Proj.rows scene
        let inBounds x y = y >= 0 && y < tiles.Length && x >= 0 && x < tiles.[y].Length
        if not (inBounds startX startY) then
            []
        else
            let sourceType = tiles.[startY].[startX].Type
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
                    if inBounds nx ny && not (seen.Contains((nx, ny))) && tiles.[ny].[nx].Type = sourceType then
                        seen.Add((nx, ny)) |> ignore
                        queue.Enqueue((nx, ny))
            List.ofSeq result
