module FarmEngine.Authoring.Tests.ProblemsTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private has (code: string) (project: GameProject) : Problem =
    let problems = Problems.collect project
    match problems |> List.tryFind (fun p -> p.Code = code) with
    | Some problem -> problem
    | None -> failwithf "expected %s in:\n%s" code (describe problems)

let private lacks (code: string) (project: GameProject) =
    let found = Problems.collect project |> List.filter (fun p -> p.Code = code)
    Assert.True(found.IsEmpty, describe found)

[<Fact>]
let ``the starter project and every template have no errors`` () =
    for (name, project) in ("starter", starter ()) :: templates () do
        let problems = errors project
        Assert.True(problems.IsEmpty, sprintf "%s: %s" name (describe problems))
        Assert.False(Problems.blocksExport (Problems.collect project))

[<Fact>]
let ``schema errors get JSON paths and targets`` () =
    Assert.Equal("scenes[0].tiles[1][2].background", Problems.jsonPath "scenes.0.tiles.1.2.background")
    Assert.Equal("settings.time.dayStartMinute", Problems.jsonPath "settings.time.dayStartMinute")
    let project = starter ()
    let broken = Records.withValue project "SelectedTileType" (box "lava")
    let problem = has "schema.selectedTileType" broken
    Assert.Equal("selectedTileType", problem.Path)
    Assert.Equal("settings", problem.TargetKind)
    Assert.True(Problems.blocksExport (Problems.collect broken))

[<Fact>]
let ``content lints map to the entry they name`` () =
    let project = starter ()
    let shop = Records.withValue (Defaults.newShop project) "Stock" (box (listOf [ ShopStockEntry(ItemId = "ghost") ]))
    let broken = project |> apply (UpsertShop shop)
    let problem = has "content.shops" broken
    Assert.Equal(sprintf "shops[%d]" (broken.Shops.Count - 1), problem.Path)
    Assert.Equal("shop", problem.TargetKind)
    Assert.Equal(shop.Id, orEmpty problem.TargetId)
    Assert.True problem.IsError

[<Fact>]
let ``unreachable dialogue and empty options`` () =
    let project = starter ()
    let orphan = Records.withValue (Defaults.newDialogue project "npc-farmer") "Options" (box (listOf [ DialogueOption(Text = "") ]))
    let broken = project |> apply (UpsertDialogue orphan)
    let unreachable = has "dialogue.unreachable" broken
    Assert.Equal("npc", unreachable.TargetKind)
    Assert.Equal("npc-farmer", orEmpty unreachable.TargetId)
    Assert.StartsWith("npcs[0].dialogue[2]", unreachable.Path)
    has "dialogue.optionEmptyText" broken |> ignore
    // Linking to it from the greeting makes it reachable.
    let greeting = broken.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-farmer-greeting")
    let linked = Records.withValue greeting "Options" (box (appended (DialogueOption(Text = "More?", NextDialogueId = orphan.Id)) greeting.Options))
    lacks "dialogue.unreachable" (broken |> apply (UpsertDialogue linked))

[<Fact>]
let ``flat dialogue list must mirror the NPC lists`` () =
    let project = starter ()
    let stray = Dialogue(Id = "dialogue-stray", NpcId = "npc-farmer", Text = "Hi", Options = listOf [ DialogueOption(Text = "Bye") ])
    let broken = Records.withValue project "Dialogues" (box (appended stray project.Dialogues))
    has "dialogue.notOnNpc" broken |> ignore
    let ghost = Records.withValue stray "NpcId" (box "npc-nobody")
    has "dialogue.npcMissing" (Records.withValue project "Dialogues" (box (appended ghost project.Dialogues))) |> ignore

[<Fact>]
let ``events with unknown flags, missing fields and out-of-bounds tiles`` () =
    let project = starter ()
    let conditions: EventCondition list = [ EnterTileCondition(X = 40.0, Y = 40.0); FlagCondition(Flag = "never-set", Value = true) ]
    let outcomes = [ EventOutcome(Type = "giveItem"); EventOutcome(Type = "performAction", ActionId = "nope") ]
    let event = Records.withValues (Defaults.newEvent project) [ ("Conditions", box (listOf conditions)); ("Outcomes", box (listOf outcomes)) ]
    let broken = project |> apply (UpsertEvent event)
    let flag = has "event.conditionUnknownFlag" broken
    Assert.Equal(sprintf "events[%d].conditions[1].flag" (broken.Events.Count - 1), flag.Path)
    Assert.Equal("event", flag.TargetKind)
    has "event.conditionTileOutOfBounds" broken |> ignore
    has "event.outcomeMissingField" broken |> ignore
    has "event.outcomeUnknownAction" broken |> ignore
    // A flag some outcome sets is known.
    let setter = Records.withValues (Defaults.newEvent broken) [ ("Outcomes", box (listOf [ EventOutcome(Type = "setFlag", FlagName = "never-set") ])) ]
    lacks "event.conditionUnknownFlag" (broken |> apply (UpsertEvent setter))

[<Fact>]
let ``doors, player start and NPC placement are bounds-checked`` () =
    let project = starter ()
    let broken = project |> apply (SetTransition("scene-farm", SceneTransition(FromX = 50.0, FromY = 1.0, ToSceneId = "scene-farm", ToX = 1.0, ToY = 1.0)))
    let problem = has "transition.fromOutOfBounds" broken
    Assert.Equal("scene", problem.TargetKind)
    let door = SceneTransition(FromX = 8.0, FromY = 11.0, ToSceneId = "scene-farm", ToX = 1.0, ToY = 1.0)
    let twoDoors = Records.withValue (farm project) "Transitions" (box (listOf [ door; door ]))
    has "transition.duplicateFrom" (Records.withValue project "Scenes" (box (listOf [ twoDoors ]))) |> ignore
    let lost = Records.withValue project "Player" (box (Records.withValues project.Player [ ("X", box 99.0) ]))
    let start = has "player.startOutOfBounds" lost
    Assert.Equal("player.x", start.Path)
    has "player.sceneMissing" (Records.withValue project "Player" (box (Records.withValue project.Player "SceneId" (box "nowhere")))) |> ignore
    let far = project |> apply (UpsertNpc(Records.withValues (Defaults.newNpc project "Far") [ ("X", box 99.0) ]))
    has "npc.outOfBounds" far |> ignore

[<Fact>]
let ``duplicate ids across every family`` () =
    let project = starter ()
    let dup = Records.withValue project "Items" (box (appended project.Items.[0] project.Items))
    let problem = has "duplicate.item" dup
    Assert.Equal(sprintf "items[%d].id" project.Items.Count, problem.Path)
    let dupAction = Records.withValue project "Actions" (box (appended project.Actions.[0] project.Actions))
    has "duplicate.action" dupAction |> ignore

[<Fact>]
let ``recipes, machines, quests and wildlife links`` () =
    let project = starter ()
    let recipe =
        Records.withValues (Defaults.newRecipe project)
            [ ("Inputs", box (listOf [ RecipeIngredient(ItemId = "ghost", Quantity = 1.0) ]))
              ("MachineTypeId", box "machine-ghost")
              ("RequiresStationCategory", box "alchemy") ]
    let broken = project |> apply (UpsertRecipe recipe)
    has "recipe.unknownInput" broken |> ignore
    has "recipe.unknownMachine" broken |> ignore
    has "recipe.unknownStation" broken |> ignore
    has "recipe.noOutputs" broken |> ignore
    let machine = has "machineType.noItem" (project |> apply (UpsertMachineType(Defaults.newMachineType project)))
    Assert.Equal("machineType", machine.TargetKind)
    let quest = Records.withValues (Defaults.newQuest project) [ ("Giver", box "npc-ghost"); ("Prerequisites", box (listOf [ "quest-1" ])) ]
    let questBroken = project |> apply (UpsertQuest quest)
    has "quest.giverMissing" questBroken |> ignore
    has "quest.prerequisiteSelf" questBroken |> ignore
    let species = Records.withValues (Defaults.newAnimalSpecies project) [ ("ProductItemId", box "ghost"); ("FeedItemId", box "ghost-feed") ]
    let wild = project |> apply (UpsertAnimalSpecies species)
    has "species.unknownProduct" wild |> ignore
    has "species.unknownFeed" wild |> ignore

[<Fact>]
let ``graphics, interface, calendar, mine and packs`` () =
    let project = starter ()
    let art = project |> apply (BindVisual(NpcVisual "npc-farmer", Some(VisualRef(AssetId = "missing-art"))))
    let missing = has "graphics.missingAsset" art
    Assert.Equal("npcs[0].visual.assetId", missing.Path)
    let panel = Records.withValue (Defaults.newGamePanel project) "Entries" (box (listOf [ GamePanelEntry(Label = "Use", Kind = "action", Value = "") ]))
    let ui = has "interface.missingAction" (project |> apply (SetGamePanels [ panel ]))
    Assert.Equal("interface", ui.TargetKind)
    let festival = CalendarFestival(Id = "festival-1", Name = "Harvest", SeasonId = "spring", Day = 40.0)
    let calendar = Records.withValue project.Settings.Calendar "Festivals" (box (listOf [ festival ]))
    has "calendar.festivalDayOutOfRange" (project |> apply (SetSettings(Records.withValue project.Settings "Calendar" (box calendar)))) |> ignore
    let mine = Records.withValues (Defaults.mineEnabled project true) [ ("EntranceSceneId", box "scene-ghost") ]
    let mined = has "mine.entranceSceneMissing" (project |> apply (SetMine mine))
    Assert.Equal("settings", mined.TargetKind)
    let pack = FarmEngine.Content.DefaultContent.CreateContentDefaultPack()
    let old = Records.withValue pack "Manifest" (box (Records.withValues pack.Manifest [ ("Id", box "old-pack"); ("EngineCompatibility", box ">=99.0.0") ]))
    let packed = has "pack.incompatible" (blank () |> apply (InstallPack old))
    Assert.Equal("pack", packed.TargetKind)
    Assert.Equal("old-pack", orEmpty packed.TargetId)

[<Fact>]
let ``reserved hotkeys and C# friendly members`` () =
    let project = starter ()
    let action = Records.withValue (Defaults.newAction project) "Hotkey" (box "w")
    let problem = has "action.reservedHotkey" (project |> apply (UpsertAction action))
    Assert.Equal("warning", problem.SeverityName)
    Assert.True problem.IsWarning
    Assert.True problem.HasTarget
    Assert.Equal("action", problem.TargetKind)
    Assert.Equal(action.Id, orEmpty problem.TargetId)
    Assert.Equal(0, problem.TargetX)
    let tile: Problem = { Severity = Severity.Info; Code = "x"; Message = "m"; Path = "p"; Target = Some(NavigationTarget.Scene("scene-farm", 3, 4)) }
    Assert.Equal("scene", tile.TargetKind)
    Assert.Equal(3, tile.TargetX)
    Assert.Equal(4, tile.TargetY)
    let nowhere: Problem = { tile with Target = None }
    Assert.Null nowhere.TargetId
