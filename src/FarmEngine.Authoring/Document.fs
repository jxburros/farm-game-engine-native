namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Core
open FarmEngine.Schemas

/// An open project plus its undo/redo history. Documents are immutable: `apply` returns a new
/// one and structural sharing keeps that cheap.
type Document =
    { Project: GameProject
      Past: GameProject list
      Future: GameProject list }

module Document =
    /// Undo depth (web `UNDO_LIMIT`).
    [<Literal>]
    let UndoLimit = 50

    let create (project: GameProject) =
        { Project = project; Past = []; Future = [] }

    let private paint (project: GameProject) (sceneId: string) (cells: (int * int) list) (tileType: string) =
        let index = project.Scenes.FindIndex(fun s -> s.Id = sceneId)
        if index < 0 then
            project
        else
            let scene = project.Scenes[index]
            let rows = scene.Tiles |> Seq.map Seq.toArray |> Seq.toArray
            let mutable changed = false
            for (x, y) in cells do
                if y >= 0 && y < rows.Length && x >= 0 && x < rows[y].Length then
                    let current = rows[y][x]
                    // Painting a layer clears whatever grew or stood on the tile (web brush).
                    let painted = Records.withValues (Tiles.SetTileLayer(current, tileType)) [ ("Crop", null); ("Node", null) ]
                    if painted <> current then
                        rows[y][x] <- painted
                        changed <- true
            if not changed then
                project
            else
                let tiles = List<List<Tile>>(rows |> Array.map (fun r -> List<Tile>(r)))
                let scenes = List<Scene>(project.Scenes)
                scenes[index] <- Records.withValue scene "Tiles" (box tiles)
                Records.withValues project [ ("Scenes", box scenes); ("SelectedTileType", box tileType) ]

    /// The project after `edit`, with no history bookkeeping (used by `apply` and by previews).
    let rec run (project: GameProject) (edit: Edit) : GameProject =
        match edit with
        | PaintTiles(sceneId, cells, tileType) -> paint project sceneId cells tileType
        | Batch(_, edits) -> edits |> List.fold run project

    /// Applies `edit`, recording one undo entry when the project changed. No-ops leave the
    /// document untouched (same instance).
    let apply (edit: Edit) (document: Document) : Document =
        let next = run document.Project edit
        if obj.ReferenceEquals(next, document.Project) then
            document
        else
            let past = document.Project :: document.Past
            let past = if past.Length > UndoLimit then List.truncate UndoLimit past else past
            { Project = next; Past = past; Future = [] }

    let canUndo (document: Document) = not document.Past.IsEmpty
    let canRedo (document: Document) = not document.Future.IsEmpty

    let undo (document: Document) : Document =
        match document.Past with
        | [] -> document
        | previous :: rest -> { Project = previous; Past = rest; Future = document.Project :: document.Future }

    let redo (document: Document) : Document =
        match document.Future with
        | [] -> document
        | next :: rest -> { Project = next; Past = document.Project :: document.Past; Future = rest }
