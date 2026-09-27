/// FsCheck properties over the edit layer (docs/LANGUAGES.md "Property tests").
module FarmEngine.Authoring.Tests.PropertyTests

open System.Collections.Generic
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private farmId = "scene-farm"

let private tileTypes = Gen.elements (List.ofSeq TileTypes.All)
let private coord = Gen.choose (-2, 18)
let private inside = Gen.choose (0, 15)

/// Edits that always change the starter project in some way, or not; the property only needs them to be valid edits.
let private simpleEdit : Gen<Edit> =
    Gen.oneof
        [ gen {
              let! x = coord
              let! y = coord
              let! t = tileTypes
              return PaintTiles(farmId, TileRules.layerOf t, [ (x, y) ], t) }
          gen {
              let! x0 = coord
              let! y0 = coord
              let! x1 = coord
              let! y1 = coord
              let! t = tileTypes
              return FillRect(farmId, TileRules.layerOf t, x0, y0, x1, y1, t) }
          gen {
              let! x = coord
              let! y = coord
              let! t = tileTypes
              return FloodFill(farmId, TileRules.layerOf t, x, y, t) }
          gen {
              let! x = inside
              let! y = inside
              return SetCollision(farmId, [ (x, y) ], true) }
          gen {
              let! x = inside
              let! y = inside
              return PlaceNode(farmId, x, y, "node-rock") }
          gen {
              let! w = Gen.choose (1, 24)
              let! h = Gen.choose (1, 24)
              return ResizeScene(farmId, w, h) }
          Gen.constant (RenameScene(farmId, "Renamed"))
          Gen.constant (SetProjectInfo("Property", "0.0.1"))
          Gen.constant (RemoveNpc "npc-farmer")
          Gen.constant (RemoveItem "material-wood")
          Gen.constant (RemoveQuest "quest-first-harvest") ]

let private edits = Arb.fromGen simpleEdit
let private editLists = Arb.fromGen (Gen.listOf simpleEdit)

[<Property(MaxTest = 60)>]
let ``apply then undo returns the original project instance`` () =
    Prop.forAll edits (fun edit ->
        let project = starter ()
        let doc = Document.create project |> Document.apply edit
        obj.ReferenceEquals((Document.undo doc).Project, project))

[<Property(MaxTest = 60)>]
let ``a batch of n edits is one undo step`` () =
    Prop.forAll editLists (fun edits ->
        let project = starter ()
        let doc = Document.create project |> Document.apply (Batch("batch", edits))
        let steps = doc.Past.Length
        (steps = 0 || steps = 1) && (steps = 0 || obj.ReferenceEquals((Document.undo doc).Project, project)))

[<Property(MaxTest = 60)>]
let ``a stroke of edits is one undo step`` () =
    Prop.forAll editLists (fun edits ->
        let project = starter ()
        let doc = edits |> List.fold (fun d e -> Document.applyInStroke "stroke" e d) (Document.create project)
        doc.Past.Length <= 1 && (doc.Past.IsEmpty || obj.ReferenceEquals((Document.undo doc).Project, project)))

[<Property(MaxTest = 60)>]
let ``resize keeps the overlap and fills the rest with grass`` () =
    Prop.forAll (Arb.fromGen (Gen.two (Gen.choose (1, 30)))) (fun (w, h) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (ResizeScene(farmId, w, h)))
        int after.Width = w && int after.Height = h && after.Tiles.Count = h
        && after.Tiles |> Seq.forall (fun row -> row.Count = w)
        && (seq {
                for y in 0 .. h - 1 do
                    for x in 0 .. w - 1 do
                        let t = after.Tiles.[y].[x]
                        if y < before.Tiles.Count && x < before.Tiles.[y].Count then yield obj.ReferenceEquals(t, before.Tiles.[y].[x])
                        else yield t.Type = "grass" && t.X = float x && t.Y = float y && t.SoilState = "dry"
            } |> Seq.forall id))

[<Property(MaxTest = 80)>]
let ``flood fill changes only the connected region of the clicked type`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.two coord) tileTypes)) (fun ((x, y), t) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (FloodFill(farmId, TileRules.layerOf t, x, y, t)))
        let inBounds = y >= 0 && y < before.Tiles.Count && x >= 0 && x < before.Tiles.[y].Count
        if not inBounds then obj.ReferenceEquals(before, after)
        else
            let source = before.Tiles.[y].[x].Type
            seq {
                for yy in 0 .. before.Tiles.Count - 1 do
                    for xx in 0 .. before.Tiles.[yy].Count - 1 do
                        let b = before.Tiles.[yy].[xx]
                        let a = after.Tiles.[yy].[xx]
                        // Tiles of another type are untouched; changed tiles were of the source type and now carry the brush.
                        yield (b.Type <> source && obj.ReferenceEquals(a, b)) || (b.Type = source && (obj.ReferenceEquals(a, b) || a.Type = t))
            } |> Seq.forall id)

[<Property(MaxTest = 80)>]
let ``fill rect never touches tiles outside the rectangle or the scene`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.four coord) tileTypes)) (fun ((x0, y0, x1, y1), t) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (FillRect(farmId, TileRules.layerOf t, x0, y0, x1, y1, t)))
        after.Tiles.Count = before.Tiles.Count
        && seq {
            for yy in 0 .. before.Tiles.Count - 1 do
                for xx in 0 .. before.Tiles.[yy].Count - 1 do
                    let b = before.Tiles.[yy].[xx]
                    let a = after.Tiles.[yy].[xx]
                    let insideRect = xx >= min x0 x1 && xx <= max x0 x1 && yy >= min y0 y1 && yy <= max y0 y1
                    yield if insideRect then a.Type = t && isNull a.Crop && isNull a.Node else obj.ReferenceEquals(a, b)
        } |> Seq.forall id)

/// Removing any entity of the starter project leaves no dangling references (no content
/// lint from the C# validator and none of the editor-level reference checks).
let private removals (project: GameProject) : Edit list =
    [ yield! project.Npcs |> Seq.map (fun n -> RemoveNpc n.Id)
      yield! project.Items |> Seq.map (fun i -> RemoveItem i.Id)
      yield! project.Quests |> Seq.map (fun q -> RemoveQuest q.Id)
      yield! project.Shops |> Seq.map (fun s -> RemoveShop s.Id)
      yield! project.Recipes |> Seq.map (fun r -> RemoveRecipe r.Id)
      yield! project.NodeTypes |> Seq.map (fun n -> RemoveNodeType n.Id)
      yield! project.MachineTypes |> Seq.map (fun m -> RemoveMachineType m.Id)
      yield! project.AnimalSpecies |> Seq.map (fun s -> RemoveAnimalSpecies s.Id)
      yield! project.FishTables |> Seq.map (fun f -> RemoveFishTable f.Id)
      yield! project.Actions |> Seq.map (fun a -> RemoveAction a.Id)
      yield! project.Minigames |> Seq.map (fun m -> RemoveMinigame m.Id)
      yield! project.Dialogues |> Seq.map (fun d -> RemoveDialogue d.Id)
      yield! project.Settings.Calendar.Seasons |> Seq.map (fun s -> RemoveSeason s.Id)
      yield! project.CustomAssets |> Seq.map (fun a -> RemoveAsset a.Id) ]

let private referenceCodes (p: Problem) =
    p.Code.StartsWith "content."
    || p.Code.Contains "Unknown" || p.Code.Contains "Missing" || p.Code.Contains "unknown" || p.Code.Contains "missing"
    || p.Code = "dialogue.npcMissing" || p.Code = "quest.giverMissing"

[<Property(MaxTest = 40)>]
let ``removing entities leaves no dangling references`` () =
    let project = starter ()
    let all = removals project |> Array.ofList
    Prop.forAll (Arb.fromGen (Gen.listOf (Gen.elements all))) (fun edits ->
        let next = edits |> List.fold (fun p e -> Document.run p e) project
        let dangling = Problems.collect next |> List.filter referenceCodes |> List.filter (fun p -> p.Severity = Severity.Error)
        if dangling.IsEmpty then true
        else failwithf "after %A:\n%s" edits (describe dangling))
