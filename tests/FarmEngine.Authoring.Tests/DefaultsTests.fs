module FarmEngine.Authoring.Tests.DefaultsTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

[<Fact>]
let ``nextId is the smallest free number and never the clock`` () =
    Assert.Equal("npc-1", Defaults.nextId "npc" [])
    Assert.Equal("npc-2", Defaults.nextId "npc" [ "npc-1"; "npc-3" ])
    Assert.Equal("npc-1", Defaults.nextId "npc" [ "npc-2" ])
    Assert.Equal(Defaults.nextId "quest" (Defaults.allIds (starter ())), Defaults.nextId "quest" (Defaults.allIds (starter ())))

[<Fact>]
let ``slugId ports the web slugify with collision suffixes`` () =
    Assert.Equal("new-action", Defaults.slugId "New Action" [] "action")
    Assert.Equal("action", Defaults.slugId "  !!  " [] "action")
    Assert.Equal("wet-season-2", Defaults.slugId "Wet Season" [ "wet-season" ] "season")
    Assert.Equal("wet-season-3", Defaults.slugId "Wet Season" [ "wet-season"; "wet-season-2" ] "season")
    Assert.Equal("mile-s-farm", Defaults.slugId "Émile's Farm!!" [] "x")

[<Fact>]
let ``new NPC sits in the middle of the current scene`` () =
    let project = starter ()
    let npc = Defaults.newNpc project "Elder"
    Assert.Equal("scene-farm", npc.SceneId)
    Assert.Equal(8.0, npc.X)
    Assert.Equal(6.0, npc.Y)
    Assert.Equal("villager", npc.Appearance)
    Assert.Equal("stationary", orEmpty npc.MovePattern)
    Assert.False npc.CanMove
    Assert.Equal("New NPC", (Defaults.newNpc project "  ").Name)

[<Fact>]
let ``defaults match the web forms`` () =
    let project = starter ()
    let crop = Defaults.newCrop project
    Assert.Equal(10.0, crop.SeedCost)
    Assert.Equal(4.0, crop.Stages)
    Assert.Equal("spring", Seq.exactlyOne crop.Seasons)
    let quest = Defaults.newQuest project
    Assert.Equal("not-started", quest.Status)
    Assert.False(quest.AutoStart.Value)
    let event = Defaults.newEvent project
    Assert.Equal(project.StartSceneId, event.SceneId)
    Assert.Equal("enter", event.Trigger)
    Assert.Equal("enterTile", event.Conditions.[0].Type)
    Assert.Equal("Something happens…", orEmpty event.Outcomes.[0].Message)
    let shop = Defaults.newShop project
    Assert.Equal(0.5, shop.RepairCostPerPoint)
    Assert.Equal("crafting", (Defaults.newRecipe project).Category)
    Assert.Equal("#9a7b4f", (Defaults.newMachineType project).Color)
    let node = Defaults.newNodeType project
    Assert.Equal("axe", node.RequiredTool)
    Assert.Equal(Some None, node.RespawnDays)
    let species = Defaults.newAnimalSpecies project
    Assert.Equal("feed-hay", orEmpty species.FeedItemId)
    Assert.Equal(500.0, species.PurchaseCost)
    Assert.Equal(0.15, (Defaults.newFishTable project).JunkChance)
    let action = Defaults.newAction project
    Assert.Equal("new-action", action.Id)
    let minigame = Defaults.newMinigame project
    Assert.Equal("timing-bar", minigame.Kind)
    Assert.Equal(JNumber 0.9, field "speed" minigame.Config)
    match Defaults.defaultCondition "timeOfDay" project with
    | EventCondition.TimeOfDay timeOfDay ->
        Assert.Equal(360.0, timeOfDay.MinMinute)
        Assert.Equal(720.0, timeOfDay.MaxMinute)
    | other -> failwithf "unexpected %A" other
    match Defaults.defaultCondition "season" project with
    | EventCondition.Season season -> Assert.Equal("spring", season.Seasons.[0])
    | other -> failwithf "unexpected %A" other
    let scene = Defaults.newSceneWith project "Cave" 8 6 "wall"
    Assert.Equal(6, scene.Tiles.Length)
    Assert.True(scene.Tiles.[0].[0].Collision)
    Assert.Equal("wall", orEmpty scene.Tiles.[0].[0].Object)

/// Every "New" result, once added to the starter project, passes validation.
let newContentEdits (project: GameProject) : (string * Edit) list =
    let npc = Defaults.newNpc project "Elder"
    let withNpc = Document.run project (UpsertNpc npc)
    let dialogue = Defaults.newDialogue withNpc npc.Id
    let species = Defaults.newAnimalSpecies project
    let withSpecies = Document.run project (UpsertAnimalSpecies species)
    let barn = Defaults.newScene project "Barn" 16 12
    [ "npc", UpsertNpc npc
      "dialogue", Batch("npc + dialogue", [ UpsertNpc npc; UpsertDialogue dialogue ])
      "item", UpsertItem(Defaults.newItem project)
      "crop", UpsertCrop(Defaults.newCrop project)
      "quest", UpsertQuest(Defaults.newQuest project)
      "event", UpsertEvent(Defaults.newEvent project)
      "shop", UpsertShop(Defaults.newShop project)
      "recipe", UpsertRecipe(Defaults.newRecipe project)
      "machineType", UpsertMachineType(Defaults.newMachineType project)
      "nodeType", UpsertNodeType(Defaults.newNodeType project)
      "animalSpecies", UpsertAnimalSpecies species
      "animal", Batch("species + animal", [ UpsertAnimalSpecies species; UpsertAnimal(Defaults.newAnimal withSpecies species.Id "scene-farm" 4 4) ])
      "fishTable", UpsertFishTable(Defaults.newFishTable project)
      "action", UpsertAction(Defaults.newAction project)
      "minigame", UpsertMinigame(Defaults.newMinigame project)
      "scene", AddScene barn
      "transition", Batch("scene + door", [ AddScene barn; SetTransition("scene-farm", Defaults.newTransition barn.Id) ])
      "panel", SetGamePanels [ Defaults.newGamePanel project ]
      "mine", SetMine(Defaults.mineEnabled project true) ]

[<Fact>]
let ``every default passes validation when added`` () =
    let project = starter ()
    for (name, edit) in newContentEdits project do
        let next = Document.run project edit
        Assert.NotSame(project, next)
        let problems = errors next
        Assert.True(problems.IsEmpty, sprintf "%s: %s" name (describe problems))

[<Fact>]
let ``defaults are deterministic`` () =
    let a = starter ()
    let b = starter ()
    Assert.Equal((Defaults.newNpc a "Elder").Id, (Defaults.newNpc b "Elder").Id)
    Assert.Equal((Defaults.newScene a "Barn" 8 6).Id, (Defaults.newScene b "Barn" 8 6).Id)
    Assert.Equal((Defaults.newEvent a).Id, (Defaults.newEvent b).Id)
