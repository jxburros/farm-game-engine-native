module FarmEngine.Authoring.Tests.SceneEditTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private farmId = "scene-farm"

[<Fact>]
let ``painting on the natural layer follows the web brush and clears crop and node`` () =
    let project = starter ()
    // (2,2) holds a starter tree.
    Assert.True((tile project farmId 2 2).Node.IsSome)
    let painted = project |> apply (PaintTiles(farmId, Background, [ (2, 2) ], "soil"))
    let t = tile painted farmId 2 2
    Assert.Equal("soil", t.Type)
    Assert.Equal("soil", t.Background)
    Assert.True t.Node.IsNone
    let walled = painted |> apply (PaintTiles(farmId, Object, [ (2, 2) ], "wall"))
    let w = tile walled farmId 2 2
    Assert.Equal("wall", orEmpty w.Object)
    Assert.Equal("soil", w.Background)
    Assert.True w.Collision

[<Fact>]
let ``painting on another layer writes that layer and the effective type`` () =
    let project = starter () |> apply (PaintTiles(farmId, Overlay, [ (1, 1) ], "floor"))
    let t = tile project farmId 1 1
    Assert.Equal("floor", orEmpty t.Overlay)
    Assert.Equal("floor", t.Type)
    Assert.Equal("grass", t.Background)

[<Fact>]
let ``erasing the object layer drops the wall and its collision`` () =
    let project = starter ()
    Assert.Equal("wall", orEmpty (tile project farmId 0 0).Object)
    let erased = project |> apply (EraseLayer(farmId, Object, [ (0, 0) ]))
    let t = tile erased farmId 0 0
    Assert.True t.Object.IsNone
    Assert.False t.Collision
    Assert.Equal("grass", t.Type)
    Assert.Same(erased, erased |> apply (EraseLayer(farmId, Object, [ (0, 0) ])))

[<Fact>]
let ``fill rect is clamped to the scene`` () =
    let project = starter () |> apply (FillRect(farmId, Background, 14, 10, 30, 30, "water"))
    let scene = farm project
    Assert.Equal("water", scene.Tiles.[11].[15].Type)
    Assert.Equal("water", scene.Tiles.[10].[14].Type)
    Assert.NotEqual<string>("water", scene.Tiles.[9].[14].Type)
    Assert.Equal(16.0, scene.Width)

[<Fact>]
let ``flood fill only touches the connected region of the clicked type`` () =
    let project = starter ()
    let before = farm project
    let count (s: Scene) (t: string) = s.Tiles |> Seq.sumBy (fun row -> row |> Seq.filter (fun x -> x.Type = t) |> Seq.length)
    let soilCount = count before "soil"
    Assert.True(soilCount > 0)
    let filled = project |> apply (FloodFill(farmId, Background, 8, 6, "water"))
    let after = farm filled
    Assert.Equal("water", after.Tiles.[6].[8].Type)
    Assert.Equal(soilCount, count after "water")
    for y in 0 .. after.Tiles.Length - 1 do
        for x in 0 .. after.Tiles.[y].Length - 1 do
            if before.Tiles.[y].[x].Type <> "soil" then Assert.Same(before.Tiles.[y].[x], after.Tiles.[y].[x])

[<Fact>]
let ``flood fill with the same type and no art is a no-op`` () =
    let project = starter ()
    Assert.Same(project, project |> apply (FloodFill(farmId, Background, 1, 1, "grass")))

[<Fact>]
let ``paste stamps the region and repositions tiles`` () =
    let project = starter ()
    let region = [ [ tile project farmId 0 0; tile project farmId 1 0 ]; [ tile project farmId 0 1; tile project farmId 1 1 ] ]
    let pasted = project |> apply (PasteTiles(farmId, 14, 10, region))
    let t = tile pasted farmId 15 11
    Assert.Equal(15.0, t.X)
    Assert.Equal(11.0, t.Y)
    Assert.Equal("grass", t.Type)
    Assert.Equal("wall", orEmpty (tile pasted farmId 14 10).Object)
    // Off the edge is dropped silently.
    let edge = project |> apply (PasteTiles(farmId, 15, 11, region))
    Assert.Equal("wall", orEmpty (tile edge farmId 15 11).Object)

[<Fact>]
let ``collision, nodes, items and machines`` () =
    let project = starter ()
    let blocked = project |> apply (SetCollision(farmId, [ (5, 5) ], true))
    Assert.True((tile blocked farmId 5 5).Collision)
    Assert.Same(blocked, blocked |> apply (SetCollision(farmId, [ (5, 5) ], true)))
    let node = project |> apply (PlaceNode(farmId, 5, 5, "node-rock"))
    let placedNode = (tile node farmId 5 5).Node.Value
    Assert.Equal("node-rock", placedNode.TypeId)
    Assert.Equal(3.0, placedNode.RemainingHealth)
    Assert.Same(project, project |> apply (PlaceNode(farmId, 5, 5, "node-unknown")))
    Assert.True((tile (node |> apply (RemoveNode(farmId, 5, 5))) farmId 5 5).Node.IsNone)
    let wood = project.Items |> Seq.find (fun i -> i.Id = "material-wood")
    let placed = project |> apply (PlaceItem(farmId, 5, 5, wood))
    Assert.Equal(Some wood, (tile placed farmId 5 5).Item)
    Assert.True((tile (placed |> apply (RemovePlacedItem(farmId, 5, 5))) farmId 5 5).Item.IsNone)
    let machine = project |> apply (PlaceMachine(farmId, 5, 5, "machine-kitchen"))
    Assert.Equal("machine-kitchen", (tile machine farmId 5 5).Machine.Value.TypeId)
    Assert.Same(project, project |> apply (PlaceMachine(farmId, 5, 5, "machine-unknown")))
    Assert.True((tile (machine |> apply (RemoveMachine(farmId, 5, 5))) farmId 5 5).Machine.IsNone)

[<Fact>]
let ``clear crops and items, reset soil and fill scene`` () =
    let wood = (starter ()).Items |> Seq.find (fun i -> i.Id = "material-wood")
    let project = starter () |> apply (PlaceItem(farmId, 5, 5, wood))
    let cleared = project |> apply (ClearCropsAndItems farmId)
    Assert.True((tile cleared farmId 5 5).Item.IsNone)
    Assert.Same(cleared, cleared |> apply (ClearCropsAndItems farmId))
    let reset = project |> apply (ResetSoil farmId)
    let anySoil = (farm reset).Tiles |> Seq.exists (fun row -> row |> Seq.exists (fun t -> t.Type = "soil"))
    Assert.False anySoil
    Assert.Equal("dry", orEmpty (tile reset farmId 8 6).SoilState)
    let filled = project |> apply (FillScene(farmId, "floor"))
    let allFloor = (farm filled).Tiles |> Seq.forall (fun row -> row |> Seq.forall (fun t -> t.Type = "floor" && t.Item.IsNone && t.Crop.IsNone))
    Assert.True allFloor

[<Fact>]
let ``add, rename, resize, duplicate and remove scenes`` () =
    let project = starter ()
    let barn = Defaults.newScene project "Barn" 8 6
    let added = project |> apply (AddScene barn)
    Assert.Equal(2, added.Scenes.Length)
    Assert.Same(added, added |> apply (AddScene barn))
    let renamed = added |> apply (RenameScene(barn.Id, "Big Barn"))
    Assert.Equal("Big Barn", (scene renamed barn.Id).Name)
    Assert.Same(added, added |> apply (RenameScene(barn.Id, "   ")))
    let resized = renamed |> apply (ResizeScene(barn.Id, 10, 4))
    let s = scene resized barn.Id
    Assert.Equal(10.0, s.Width)
    Assert.Equal(4, s.Tiles.Length)
    Assert.True(s.Tiles |> Seq.forall (fun row -> row.Length = 10))
    Assert.Equal("dry", orEmpty s.Tiles.[3].[9].SoilState)
    Assert.Same(resized, resized |> apply (ResizeScene(barn.Id, 10, 4)))
    let copied = resized |> apply (DuplicateScene(barn.Id, "scene-barn-copy"))
    Assert.Equal("Big Barn (Copy)", (scene copied "scene-barn-copy").Name)
    Assert.Empty((scene copied "scene-barn-copy").Npcs)
    let removed = copied |> apply (RemoveScene "scene-barn-copy")
    Assert.Equal(2, removed.Scenes.Length)
    // Never the start scene, never the last scene.
    Assert.Same(removed, removed |> apply (RemoveScene farmId))
    let only = project |> apply (RemoveScene farmId)
    Assert.Same(project, only)

[<Fact>]
let ``removing a scene drops doors into it, its NPCs and events, and moves the player`` () =
    let project = starter ()
    let barn = Defaults.newScene project "Barn" 8 6
    let door = { SceneTransition.Default with FromX = 8.0; FromY = 11.0; ToSceneId = barn.Id; ToX = 4.0; ToY = 5.0 }
    let goat = { (Defaults.newNpc project "Goat") with SceneId = barn.Id }
    let warpOutcome = { EventOutcome.Default with Type = "warpPlayer"; SceneId = Some(barn.Id); X = Some(1.0); Y = Some(1.0) }
    let warp = { Defaults.newEvent project with SceneId = farmId; Outcomes = [ warpOutcome ] }
    let inside = { Defaults.newEvent project with Id = "event-inside"; SceneId = barn.Id }
    let setup = Batch("setup", [ AddScene barn; SetTransition(farmId, door); UpsertNpc goat; UpsertEvent warp; UpsertEvent inside; SetPlayerStart(barn.Id, 2, 2) ])
    let built = project |> apply setup
    Assert.Equal(barn.Id, built.Player.SceneId)
    let removed = built |> apply (RemoveScene barn.Id)
    Assert.Equal(1, removed.Scenes.Length)
    Assert.False((farm removed).Transitions |> Seq.exists (fun t -> t.ToSceneId = barn.Id))
    Assert.False(removed.Npcs |> Seq.exists (fun n -> n.Id = goat.Id))
    Assert.False(removed.Events |> Seq.exists (fun e -> e.Id = inside.Id))
    Assert.Empty((removed.Events |> Seq.find (fun e -> e.Id = warp.Id)).Outcomes)
    Assert.Equal(farmId, removed.Player.SceneId)
    let leftovers = errors removed |> List.filter (fun p -> p.Code.StartsWith "content." || p.Code.StartsWith "schema.")
    Assert.True(leftovers.IsEmpty, describe leftovers)

[<Fact>]
let ``transitions are keyed by their departure tile`` () =
    let project = starter ()
    let barn = Defaults.newScene project "Barn" 8 6
    let project = project |> apply (AddScene barn)
    let door = { SceneTransition.Default with FromX = 8.0; FromY = 11.0; ToSceneId = barn.Id; ToX = 4.0; ToY = 5.0 }
    let withDoor = project |> apply (SetTransition(farmId, door))
    Assert.Equal(1, (farm withDoor).Transitions.Length)
    let moved = withDoor |> apply (SetTransition(farmId, { door with ToX = 3.0 }))
    Assert.Equal(1, (farm moved).Transitions.Length)
    Assert.Equal(3.0, (farm moved).Transitions.[0].ToX)
    Assert.Same(moved, moved |> apply (SetTransition(farmId, { door with ToX = 3.0 })))
    let linked = project |> apply (Defaults.linkScenes farmId door)
    Assert.Equal(1, (farm linked).Transitions.Length)
    Assert.Equal(1, (scene linked barn.Id).Transitions.Length)
    Assert.Equal(farmId, (scene linked barn.Id).Transitions.[0].ToSceneId)
    let removed = moved |> apply (RemoveTransition(farmId, 8, 11))
    Assert.Empty((farm removed).Transitions)
    Assert.Same(removed, removed |> apply (ClearTransitions farmId))
    Assert.Empty((farm (linked |> apply (ClearTransitions farmId))).Transitions)

[<Fact>]
let ``start scene, player start and brush`` () =
    let project = starter ()
    Assert.Same(project, project |> apply (SetStartScene "nope"))
    Assert.Same(project, project |> apply (SetStartScene farmId))
    let barn = Defaults.newScene project "Barn" 8 6
    let added = project |> apply (AddScene barn)
    Assert.Equal(barn.Id, (added |> apply (SetStartScene barn.Id)).StartSceneId)
    let moved = added |> apply (SetPlayerStart(barn.Id, 3, 2))
    Assert.Equal(barn.Id, moved.Player.SceneId)
    Assert.Equal(3.0, moved.Player.X)
    Assert.Same(moved, moved |> apply (SetPlayerStart(barn.Id, 3, 2)))
    Assert.Same(added, added |> apply (SetPlayerStart("nope", 1, 1)))
    let brush = project |> apply (SelectBrush("water", None))
    Assert.Equal("water", brush.SelectedTileType)
    Assert.Equal("tiles", brush.Mode)
    Assert.Same(brush, brush |> apply (SelectBrush("water", None)))

[<Fact>]
let ``the brush art rides along when painting the selected type`` () =
    let project = starter ()
    let asset = { CustomAsset.Default with Id = "art-1"; Name = "tiles.png"; Type = "art"; DataUrl = "data:,"; Width = Some(32.0); Height = Some(32.0) }
    let visual = { VisualRef.Default with AssetId = "art-1" }
    let painted = project |> apply (Batch("paint art", [ UpsertAsset asset; SelectBrush("water", Some visual); PaintTiles(farmId, Background, [ (3, 3) ], "water") ]))
    Assert.Equal(Some visual, (tile painted farmId 3 3).Visuals.Value.Background)
    let plain = painted |> apply (PaintTiles(farmId, Background, [ (4, 4) ], "soil"))
    Assert.True((tile plain farmId 4 4).Visuals.IsNone)
    let cleared = painted |> apply (SetTileVisual(farmId, Background, [ (3, 3) ], None))
    Assert.True((tile cleared farmId 3 3).Visuals.IsNone)
    let art = errors cleared |> List.filter (fun p -> p.Code.StartsWith "graphics.")
    Assert.True(art.IsEmpty, describe art)

[<Fact>]
let ``the eyedropper picks the tile type and the art on that type's layer`` () =
    let project = starter ()
    let visual = { VisualRef.Default with AssetId = "art-path" }
    let painted = project |> apply (Batch("path art", [ PaintTiles(farmId, Overlay, [ (4, 4) ], "path"); SetTileVisual(farmId, Overlay, [ (4, 4) ], Some visual) ]))
    match nonNull (Edits.PickBrush(painted, farmId, 4, 4)) with
    | SelectBrush(tileType, picked) ->
        Assert.Equal("path", tileType)
        Assert.Equal(Some visual, picked)
    | other -> failwithf "expected SelectBrush, got %A" other
    let brush = painted |> apply (nonNull (Edits.PickBrush(painted, farmId, 4, 4)))
    Assert.Equal("path", brush.SelectedTileType)
    Assert.Equal(Some visual, brush.SelectedTileVisual)
    // Art on another layer is not picked up.
    match nonNull (Edits.PickBrush(painted |> apply (PaintTiles(farmId, Background, [ (4, 4) ], "grass")), farmId, 4, 4)) with
    | SelectBrush(tileType, picked) ->
        Assert.Equal("grass", tileType)
        Assert.True(picked.IsNone)
    | other -> failwithf "expected SelectBrush, got %A" other
    Assert.Null(Edits.PickBrush(painted, farmId, 99, 0))
    Assert.Null(Edits.PickBrush(painted, "nope", 0, 0))

[<Fact>]
let ``the remove tool clears what was placed on a tile and the animals standing there`` () =
    let project = starter ()
    let wood = project.Items |> Seq.find (fun i -> i.Id = "material-wood")
    let species = project.AnimalSpecies.Head.Id
    let here = Defaults.newAnimal project species farmId 5 5
    let placed =
        project
        |> apply (Batch("place", [ PlaceNode(farmId, 5, 5, "node-rock"); PlaceItem(farmId, 5, 5, wood); PlaceMachine(farmId, 5, 5, "machine-kitchen")
                                   UpsertAnimal here; MoveNpc("npc-farmer", farmId, 5, 5) ]))
    let neighbour = Defaults.newAnimal placed species farmId 6 5
    let placed = placed |> apply (UpsertAnimal neighbour)
    let cleared = placed |> apply (ClearTile(farmId, 5, 5))
    let t = tile cleared farmId 5 5
    Assert.True t.Node.IsNone
    Assert.True t.Item.IsNone
    Assert.True t.Machine.IsNone
    Assert.DoesNotContain(cleared.Animals, fun a -> a.Id = here.Id)
    Assert.Contains(cleared.Animals, fun a -> a.Id = neighbour.Id)
    // NPCs are moved on the map, never deleted from it.
    Assert.Equal(5.0, (npc cleared "npc-farmer").X)
    Assert.Same(cleared, cleared |> apply (ClearTile(farmId, 5, 5)))
    Assert.Same(placed, placed |> apply (ClearTile(farmId, 99, 5)))
    Assert.Same(placed, placed |> apply (ClearTile("nope", 5, 5)))

[<Fact>]
let ``the place tools offer the project's content and put it on the clicked tile`` () =
    let project = starter ()
    let ids kind = MapPlacement.Choices(kind, project) |> Seq.map (fun o -> o.Id) |> List.ofSeq
    Assert.Contains("npc-farmer", ids "npc")
    Assert.Contains("node-rock", ids "nodeType")
    Assert.Equal((ids "nodeType").Length, (ids "nodeType" |> List.distinct).Length)
    Assert.Contains("material-wood", ids "item")
    Assert.Contains("machine-kitchen", ids "machineType")
    Assert.Equal<string list>(project.AnimalSpecies |> Seq.map (fun s -> s.Id) |> List.ofSeq, ids "animalSpecies")
    Assert.Empty(MapPlacement.Choices("scene", project))

    let place kind id = project |> apply (nonNull (MapPlacement.Place(project, kind, id, farmId, 5, 5)))
    let farmer = npc (place "npc" "npc-farmer") "npc-farmer"
    Assert.Equal((farmId, 5.0, 5.0), (farmer.SceneId, farmer.X, farmer.Y))
    Assert.Equal("node-rock", (tile (place "nodeType" "node-rock") farmId 5 5).Node.Value.TypeId)
    Assert.Equal("material-wood", (tile (place "item" "material-wood") farmId 5 5).Item.Value.Id)
    Assert.Equal("machine-kitchen", (tile (place "machineType" "machine-kitchen") farmId 5 5).Machine.Value.TypeId)
    let species = project.AnimalSpecies.Head
    let withAnimal = place "animalSpecies" species.Id
    Assert.Equal(project.Animals.Length + 1, withAnimal.Animals.Length)
    let animal = withAnimal.Animals |> Seq.last
    Assert.Equal((species.Id, species.Name, farmId, 5.0, 5.0), (animal.SpeciesId, animal.Name, animal.SceneId, animal.X, animal.Y))
    Assert.Null(MapPlacement.Place(project, "item", "item-unknown", farmId, 5, 5))
    Assert.Null(MapPlacement.Place(project, "scene", farmId, farmId, 5, 5))

/// The starter farm with something standing near every edge of it: a second scene whose door
/// lands at the farm's far corner, an animal, an NPC schedule stop and waypoint, an event tile
/// with a tile change and a warp, and the mine entrance (#44).
let private crowdedFarm () =
    let project = starter ()
    let barn = Defaults.newScene project "Barn" 8 6
    let door = { SceneTransition.Default with FromX = 1.0; FromY = 1.0; ToSceneId = farmId; ToX = 15.0; ToY = 11.0 }
    let farmDoor = { SceneTransition.Default with FromX = 14.0; FromY = 10.0; ToSceneId = barn.Id; ToX = 2.0; ToY = 2.0 }
    let species = project.AnimalSpecies.Head
    let first = project.Npcs.Head
    let walker =
        { first with
            X = 13.0; Y = 9.0
            Schedule = Some [ { NpcScheduleEntry.Default with Minute = 480.0; SceneId = farmId; X = 15.0; Y = 2.0 } ]
            PatrolPoints = Some [ { GridPoint.X = 1.0; Y = 1.0 }; { GridPoint.X = 14.0; Y = 11.0 } ] }
    let event =
        { GameEvent.Default with
            Id = "event-edge"; Name = "Edge"; SceneId = farmId; Trigger = EventTriggers.Interact; Active = true
            Conditions = [ EventCondition.InteractTile { InteractTileCondition.Default with X = 12.0; Y = 10.0; X2 = Some 15.0; Y2 = Some 11.0 } ]
            Outcomes =
                [ { EventOutcome.Default with Type = EventOutcomeTypes.ChangeTile; TileX = Some 15.0; TileY = Some 11.0; NewTileType = Some "water" }
                  { EventOutcome.Default with Type = EventOutcomeTypes.WarpPlayer; SceneId = Some farmId; X = Some 15.0; Y = Some 11.0 } ] }
    let placed =
        project
        |> apply (Batch("crowd", [ AddScene barn; SetTransition(barn.Id, door); SetTransition(farmId, farmDoor); SetPlayerStart(farmId, 15, 11); UpsertNpc walker; UpsertEvent event ]))
    let placed = placed |> apply (nonNull (MapPlacement.Place(placed, "animalSpecies", species.Id, farmId, 14, 11)))
    let placed = placed |> apply (SetMine { placed.Mine with Enabled = true; EntranceSceneId = Some farmId; EntranceX = Some 15.0; EntranceY = Some 11.0 })
    placed, barn.Id, walker.Id

let private outOfBounds (project: GameProject) =
    errors project |> List.filter (fun p -> p.Code.ToLowerInvariant().Contains "outofbounds" || p.Message.Contains "out of bounds")

[<Fact>]
let ``shrinking a scene moves the player, NPCs, doors, events, animals and the mine entrance inside`` () =
    let project, barnId, npcId = crowdedFarm ()
    let before = outOfBounds project
    Assert.True(before.IsEmpty, describe before)
    let impact = Edits.ResizeImpact(project, farmId, 10, 8) |> List.ofSeq
    Assert.Contains("the player start moves inside", impact)
    Assert.Contains("the door at (14, 10) is removed", impact)
    Assert.Contains("the mine entrance moves inside", impact)
    Assert.Contains(impact, fun (line: string) -> line.StartsWith "NPC ")
    Assert.Contains(impact, fun (line: string) -> line.StartsWith "animal ")
    Assert.Contains(impact, fun (line: string) -> line.StartsWith "event ")
    Assert.Contains(impact, fun (line: string) -> line.StartsWith "the door from \"Barn\"")
    Assert.Empty(Edits.ResizeImpact(project, farmId, 20, 20))

    let shrunk = project |> apply (ResizeScene(farmId, 10, 8))
    let after = outOfBounds shrunk
    Assert.True(after.IsEmpty, describe after)
    Assert.Equal((9.0, 7.0), (shrunk.Player.X, shrunk.Player.Y))
    let walker = npc shrunk npcId
    Assert.Equal((9.0, 7.0), (walker.X, walker.Y))
    Assert.Equal((9.0, 2.0), (walker.Schedule.Value.Head.X, walker.Schedule.Value.Head.Y))
    Assert.Equal<GridPoint list>([ { GridPoint.X = 1.0; Y = 1.0 }; { GridPoint.X = 9.0; Y = 7.0 } ], walker.PatrolPoints.Value)
    Assert.Empty((farm shrunk).Transitions)
    let landing = (scene shrunk barnId).Transitions.Head
    Assert.Equal((9.0, 7.0), (landing.ToX, landing.ToY))
    let animal = shrunk.Animals |> Seq.last
    Assert.Equal((9.0, 7.0), (animal.X, animal.Y))
    let event = shrunk.Events |> Seq.find (fun e -> e.Id = "event-edge")
    match event.Conditions.Head with
    | EventCondition.InteractTile t -> Assert.Equal((9.0, 7.0, Some 9.0, Some 7.0), (t.X, t.Y, t.X2, t.Y2))
    | other -> failwithf "expected an interact tile, got %A" other
    Assert.Equal((Some 9.0, Some 7.0), (event.Outcomes.[0].TileX, event.Outcomes.[0].TileY))
    Assert.Equal((Some 9.0, Some 7.0), (event.Outcomes.[1].X, event.Outcomes.[1].Y))
    Assert.Equal((Some 9.0, Some 7.0), (shrunk.Mine.EntranceX, shrunk.Mine.EntranceY))
    // Growing back moves nothing.
    let grown = shrunk |> apply (ResizeScene(farmId, 16, 12))
    Assert.Equal((9.0, 7.0), (grown.Player.X, grown.Player.Y))

[<Fact>]
let ``shrinking the starter farm leaves no out-of-bounds problems`` () =
    for width, height in [ (1, 1); (4, 3); (8, 12); (16, 2) ] do
        let shrunk = starter () |> apply (ResizeScene(farmId, width, height))
        let after = outOfBounds shrunk
        Assert.True(after.IsEmpty, sprintf "%dx%d:\n%s" width height (describe after))

[<Fact>]
let ``resize ignores sizes outside 1 to 256`` () =
    let project = starter ()
    Assert.Same(project, project |> apply (ResizeScene(farmId, 0, 5)))
    Assert.Same(project, project |> apply (ResizeScene(farmId, 257, 5)))
    Assert.Same(project, project |> apply (ResizeScene(farmId, 5, 300)))
    Assert.Equal(256.0, (farm (project |> apply (ResizeScene(farmId, 256, 12)))).Width)

[<Fact>]
let ``a resize keeps the rows that keep their width`` () =
    let project = starter ()
    let taller = project |> apply (ResizeScene(farmId, 16, 20))
    Assert.True(Seq.forall2 (fun (a: Tile list) b -> obj.ReferenceEquals(a, b)) (farm project).Tiles ((farm taller).Tiles |> List.truncate 12))
