module FarmEngine.Authoring.Tests.DocumentTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Content

[<Fact>]
let ``painting a tile records one undo entry`` () =
    let project = DefaultContent.CreateInitialProject(0.0)
    let doc = Document.create project
    let painted = doc |> Document.apply (PaintTiles("scene-farm", [ (1, 1) ], "water"))
    Assert.True(Document.canUndo painted)
    Assert.Equal("water", painted.Project.Scenes.[0].Tiles.[1].[1].Background)
    let undone = Document.undo painted
    Assert.Same(project, undone.Project)
    Assert.True(Document.canRedo undone)
