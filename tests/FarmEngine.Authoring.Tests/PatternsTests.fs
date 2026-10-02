module FarmEngine.Authoring.Tests.PatternsTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private options : PatternOptions =
    { Name = "Willow"; Text = "A quiet spot."; X = 5; Y = 5; Day = 2; NpcId = "npc-farmer"; Friendship = 500; Consequences = true; SceneId = "" }

let private build (kind: PatternKind) (opts: PatternOptions) (project: GameProject) : Edit =
    match Patterns.build kind opts project with
    | Ok edit -> edit
    | Error message -> failwithf "%A: %s" kind message

[<Fact>]
let ``every pattern applies as one batch and leaves a valid project`` () =
    let project = starter ()
    for info in Patterns.all do
        let edit = build info.Kind options project
        match edit with
        | Batch(label, edits) ->
            Assert.Equal(info.Name, label)
            Assert.NotEmpty edits
        | other -> failwithf "%s is not a batch: %A" info.Id other
        let doc = Document.create project |> Document.apply edit
        Assert.Equal(1, doc.Past.Length)
        let problems = errors doc.Project
        Assert.True(problems.IsEmpty, sprintf "%s: %s" info.Id (describe problems))

[<Fact>]
let ``the patterns create what the web creates`` () =
    let project = starter ()
    let story = project |> apply (build Story options project)
    let willow = npc story "willow"
    Assert.Equal(3, willow.Dialogue.Length)
    Assert.Equal("willow-promised", orEmpty willow.Dialogue.[0].Options.[0].EventFlag)
    let romance = project |> apply (build Romance options project)
    Assert.True(romance.Actions |> Seq.exists (fun a -> a.Id = "willow-action"))
    Assert.True((npc romance "npc-farmer").Dialogue.[0].Options |> Seq.exists (fun o -> orEmpty o.ActionId = "willow-action"))
    let mail = project |> apply (build Mail options project)
    Assert.True(mail.Items |> Seq.exists (fun i -> i.Id = "willow-item" && i.Type = "quest"))
    let building = project |> apply (build Building options project)
    Assert.True(building.Scenes |> Seq.exists (fun s -> s.Id = "willow-inside"))
    Assert.Equal("door", orEmpty (tile building "scene-farm" 5 5).Object)
    Assert.True((farm building).Transitions |> Seq.exists (fun t -> t.ToSceneId = "willow-inside" && t.Locked = Some true))
    let magic = project |> apply (build Magic options project)
    Assert.Equal("willow-item", (tile magic "scene-farm" 5 5).Item.Value.Id)
    let fishing = project |> apply (build Fishing options project)
    let minigame = fishing.Minigames |> Seq.find (fun m -> m.Id = "fishing")
    Assert.Equal("hold-to-catch", minigame.Kind)
    Assert.Equal(JNumber 1200.0, field "holdMs" minigame.Config)
    // Only settings hold-to-catch reads stay (the starter timing bar's speed and prompt go).
    Assert.Equal<string list>([ "holdMs" ], minigame.Config |> List.map fst)
    let combat = project |> apply (build Combat options project)
    Assert.Equal("simple-battle", (combat.Minigames |> Seq.find (fun m -> m.Id = "willow")).Kind)
    let tree = project |> apply (build Tree options project)
    let node = (tile tree "scene-farm" 5 5).Node.Value
    Assert.Equal("willow", node.TypeId)
    Assert.Equal(3.0, node.RemainingHealth)
    let craft = project |> apply (build Craft options project)
    Assert.True(craft.MachineTypes |> Seq.exists (fun m -> m.Id = "willow" && orEmpty m.ItemId = "willow-item"))
    Assert.True(craft.Recipes |> Seq.exists (fun r -> r.Id = "willow-product-recipe" && orEmpty r.RequiresStationCategory = "willow"))

[<Fact>]
let ``pattern ids avoid existing content`` () =
    let first = starter ()
    let project = first |> apply (build Tree options first)
    let again = project |> apply (build Tree { options with X = 6 } project)
    Assert.True(again.NodeTypes |> Seq.exists (fun n -> n.Id = "willow-2"))

[<Fact>]
let ``patterns refuse bad tiles with the web messages`` () =
    let project = starter ()
    expectError "Choose a tile inside the current scene." (Patterns.build Story { options with X = 99 } project)
    expectError "That tile already has a gathering node." (Patterns.build Tree { options with X = 2; Y = 2 } project)
    expectError "Choose a character with a starting dialogue." (Patterns.build Romance { options with NpcId = "nobody" } project)
    let magic = project |> apply (build Magic options project)
    expectError "That tile already has an item. Choose an empty tile." (Patterns.build Magic options magic)
    let building = project |> apply (build Building options project)
    expectError "That tile already has a doorway." (Patterns.build Building options building)
    let noScenes = { project with Scenes = [] }
    expectError "Select a scene first." (Patterns.build Story options noScenes)

[<Fact>]
let ``patterns put their tile in the scene the map shows`` () =
    let first = starter ()
    let barn = Defaults.newScene first "Barn" 8 6
    let project = first |> apply (AddScene barn)
    let mail = project |> apply (build Mail { options with SceneId = barn.Id; X = 3; Y = 2 } project)
    let event = mail.Events |> Seq.last
    Assert.Equal(barn.Id, event.SceneId)
    // An unknown scene falls back to the player's scene.
    let fallback = project |> apply (build Mail { options with SceneId = "nope" } project)
    Assert.Equal("scene-farm", (fallback.Events |> Seq.last).SceneId)
