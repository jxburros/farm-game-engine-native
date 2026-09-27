module FarmEngine.Authoring.Tests.DocumentTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private paint x y tileType = PaintTiles("scene-farm", TileRules.layerOf tileType, [ (x, y) ], tileType)

[<Fact>]
let ``painting a tile records one undo entry`` () =
    let project = starter ()
    let doc = Document.create project
    let painted = doc |> Document.apply (paint 1 1 "water")
    Assert.True(Document.canUndo painted)
    Assert.Equal("water", painted.Project.Scenes.[0].Tiles.[1].[1].Background)
    Assert.Equal("water", painted.Project.SelectedTileType)
    let undone = Document.undo painted
    Assert.Same(project, undone.Project)
    Assert.True(Document.canRedo undone)
    Assert.Same(painted.Project, (Document.redo undone).Project)

[<Fact>]
let ``a no-op edit returns the same document`` () =
    let doc = Document.create (starter ())
    let same = doc |> Document.apply (RenameScene("scene-farm", "Farm"))
    Assert.Same(doc, same)
    Assert.False(Document.canUndo same)
    let missing = doc |> Document.apply (paint 99 99 "water")
    Assert.Same(doc, missing)

[<Fact>]
let ``a batch is one undo step`` () =
    let doc = Document.create (starter ())
    let batch = Batch("three tiles", [ paint 1 1 "water"; paint 2 1 "water"; paint 3 1 "water" ])
    let applied = doc |> Document.apply batch
    Assert.Equal(1, applied.Past.Length)
    Assert.Equal("water", (tile applied.Project "scene-farm" 3 1).Type)
    let undone = Document.undo applied
    Assert.Same(doc.Project, undone.Project)
    Assert.Equal("grass", (tile undone.Project "scene-farm" 3 1).Type)

[<Fact>]
let ``an empty batch or a batch of no-ops is not recorded`` () =
    let doc = Document.create (starter ())
    Assert.Same(doc, doc |> Document.apply (Batch("nothing", [])))
    Assert.Same(doc, doc |> Document.apply (Batch("still nothing", [ RenameScene("scene-farm", "Farm"); RemoveNpc "nobody" ])))

[<Fact>]
let ``history is capped at the undo limit`` () =
    let mutable doc = Document.create (starter ())
    for i in 0 .. Document.UndoLimit + 9 do
        let tileType = if i % 2 = 0 then "water" else "soil"
        doc <- doc |> Document.apply (paint 1 1 tileType)
    Assert.Equal(Document.UndoLimit, doc.Past.Length)

[<Fact>]
let ``a new edit clears the redo stack`` () =
    let doc = Document.create (starter ()) |> Document.apply (paint 1 1 "water") |> Document.undo
    Assert.True(Document.canRedo doc)
    let branched = doc |> Document.apply (paint 2 2 "soil")
    Assert.False(Document.canRedo branched)

[<Fact>]
let ``a drag stroke is one undo entry`` () =
    let project = starter ()
    let doc = Document.create project
    let stroked =
        doc
        |> Document.applyInStroke "s1" (paint 1 1 "water")
        |> Document.applyInStroke "s1" (paint 2 1 "water")
        |> Document.applyInStroke "s1" (paint 3 1 "water")
    Assert.Equal(1, stroked.Past.Length)
    Assert.Equal(Some "s1", stroked.Stroke)
    Assert.Equal("water", (tile stroked.Project "scene-farm" 3 1).Type)
    let undone = Document.undo stroked
    Assert.Same(project, undone.Project)
    Assert.Equal(None, undone.Stroke)

[<Fact>]
let ``a second stroke starts a new entry and a plain edit ends the stroke`` () =
    let doc = Document.create (starter ())
    let first = doc |> Document.applyInStroke "s1" (paint 1 1 "water") |> Document.applyInStroke "s1" (paint 2 1 "water")
    let second = first |> Document.applyInStroke "s2" (paint 1 2 "soil")
    Assert.Equal(2, second.Past.Length)
    let plain = second |> Document.apply (paint 5 5 "path")
    Assert.Equal(None, plain.Stroke)
    Assert.Equal(3, plain.Past.Length)
    let ended = Document.endStroke second
    Assert.Equal(None, ended.Stroke)
    let again = ended |> Document.applyInStroke "s2" (paint 4 4 "soil")
    Assert.Equal(3, again.Past.Length)

[<Fact>]
let ``a stroke whose first edits are no-ops pushes history on the first real change`` () =
    let doc = Document.create (starter ())
    let stroked = doc |> Document.applyInStroke "s1" (paint 99 99 "water")
    Assert.Same(doc, stroked)
    let changed = stroked |> Document.applyInStroke "s1" (paint 1 1 "water")
    Assert.Equal(1, changed.Past.Length)

[<Fact>]
let ``replace project is one undo step even for imports`` () =
    let project = starter ()
    let other = blank ()
    let doc = Document.create project |> Document.apply (ReplaceProject other)
    Assert.Same(other, doc.Project)
    Assert.Same(project, (Document.undo doc).Project)
    Assert.Same(doc, doc |> Document.apply (ReplaceProject other))

[<Fact>]
let ``run gives a preview without touching history`` () =
    let project = starter ()
    let preview = Document.run project (paint 1 1 "water")
    Assert.NotSame(project, preview)
    Assert.Equal("grass", (tile project "scene-farm" 1 1).Type)
    Assert.Equal("water", (tile preview "scene-farm" 1 1).Type)
