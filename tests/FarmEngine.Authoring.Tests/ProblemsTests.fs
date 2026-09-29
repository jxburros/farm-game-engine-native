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
let ``export settings accept valid defaults and reject broken identities and targets`` () =
    let project = starter ()
    let settings = Defaults.newExportSettings project
    let valid = project |> apply (SetExportSettings(Some settings))
    Assert.Empty(Problems.collect valid |> List.filter (fun p -> p.Code.StartsWith "export."))
    let broken =
        { settings with
            GameId = "Bad Id"
            ExecutableName = Some "CON"
            Window = { ExportWindow.Default with Width = 200 }
            PixelScale = "stretch"
            Targets = [ "windows-x64"; "windows-x64"; "macos" ]
            IconAssetId = Some "missing" }
        |> fun bad -> valid |> apply (SetExportSettings(Some bad))
    for code in [ "export.gameId"; "export.executableName"; "export.window"; "export.pixelScale"; "export.target"; "export.targets"; "export.icon" ] do
        let problem = has code broken
        Assert.Equal("settings", problem.TargetKind)
        Assert.True problem.IsError
    Assert.True(Problems.blocksExport (Problems.collect broken))

[<Fact>]
let ``export icon must be large PNG artwork`` () =
    let project = starter ()
    let small = { CustomAsset.Default with Id = "icon-1"; Name = "icon.png"; Type = "art"; DataUrl = "data:image/png;base64,AA=="; Width = Some(128.0); Height = Some(128.0) }
    let withAsset = project |> apply (UpsertAsset small)
    let settings = { (Defaults.newExportSettings withAsset) with IconAssetId = Some small.Id }
    let withIcon = withAsset |> apply (SetExportSettings(Some settings))
    has "export.icon" withIcon |> ignore
    let large = { small with Width = Some 256.0; Height = Some 256.0 }
    lacks "export.icon" (withIcon |> apply (UpsertAsset large))

[<Fact>]
let ``schema errors get JSON paths and targets`` () =
    Assert.Equal("scenes[0].tiles[1][2].background", Problems.jsonPath "scenes.0.tiles.1.2.background")
    Assert.Equal("settings.time.dayStartMinute", Problems.jsonPath "settings.time.dayStartMinute")
    let project = starter ()
    let broken = { project with SelectedTileType = "lava" }
    let problem = has "schema.selectedTileType" broken
    Assert.Equal("selectedTileType", problem.Path)
    Assert.Equal("settings", problem.TargetKind)
    Assert.True(Problems.blocksExport (Problems.collect broken))

[<Fact>]
let ``content lints map to the entry they name`` () =
    let project = starter ()
    let shop = { (Defaults.newShop project) with Stock = ([ { ShopStockEntry.Default with ItemId = "ghost" } ]) }
    let broken = project |> apply (UpsertShop shop)
    let problem = has "content.shops" broken
    Assert.Equal(sprintf "shops[%d]" (broken.Shops.Length - 1), problem.Path)
    Assert.Equal("shop", problem.TargetKind)
    Assert.Equal(shop.Id, problem.TargetId)
    Assert.True problem.IsError

[<Fact>]
let ``unreachable dialogue and empty options`` () =
    let project = starter ()
    let orphan = { (Defaults.newDialogue project "npc-farmer") with Options = ([ { DialogueOption.Default with Text = "" } ]) }
    let broken = project |> apply (UpsertDialogue orphan)
    let unreachable = has "dialogue.unreachable" broken
    Assert.Equal("npc", unreachable.TargetKind)
    Assert.Equal("npc-farmer", unreachable.TargetId)
    Assert.StartsWith("npcs[0].dialogue[2]", unreachable.Path)
    has "dialogue.optionEmptyText" broken |> ignore
    // Linking to it from the greeting makes it reachable.
    let greeting = broken.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-farmer-greeting")
    let linked = { greeting with Options = (greeting.Options @ [ ({ DialogueOption.Default with Text = "More?"; NextDialogueId = Some(orphan.Id) }) ]) }
    lacks "dialogue.unreachable" (broken |> apply (UpsertDialogue linked))

[<Fact>]
let ``flat dialogue list must mirror the NPC lists`` () =
    let project = starter ()
    let stray = { Dialogue.Default with Id = "dialogue-stray"; NpcId = "npc-farmer"; Text = "Hi"; Options = [ { DialogueOption.Default with Text = "Bye" } ] }
    let broken = { project with Dialogues = (project.Dialogues @ [ stray ]) }
    has "dialogue.notOnNpc" broken |> ignore
    let ghost = { stray with NpcId = "npc-nobody" }
    has "dialogue.npcMissing" ({ project with Dialogues = (project.Dialogues @ [ ghost ]) }) |> ignore

[<Fact>]
let ``events with unknown flags, missing fields and out-of-bounds tiles`` () =
    let project = starter ()
    let conditions: EventCondition list =
        [ EventCondition.EnterTile { EnterTileCondition.Default with X = 40.0; Y = 40.0 }
          EventCondition.Flag { FlagCondition.Default with Flag = "never-set"; Value = true } ]
    let outcomes = [ { EventOutcome.Default with Type = "giveItem" }; { EventOutcome.Default with Type = "performAction"; ActionId = Some("nope") } ]
    let event = { Defaults.newEvent project with Conditions = conditions; Outcomes = outcomes }
    let broken = project |> apply (UpsertEvent event)
    let flag = has "event.conditionUnknownFlag" broken
    Assert.Equal(sprintf "events[%d].conditions[1].flag" (broken.Events.Length - 1), flag.Path)
    Assert.Equal("event", flag.TargetKind)
    has "event.conditionTileOutOfBounds" broken |> ignore
    has "event.outcomeMissingField" broken |> ignore
    has "event.outcomeUnknownAction" broken |> ignore
    // A flag some outcome sets is known.
    let setter = { Defaults.newEvent broken with Outcomes = [ { EventOutcome.Default with Type = "setFlag"; FlagName = Some("never-set") } ] }
    lacks "event.conditionUnknownFlag" (broken |> apply (UpsertEvent setter))

[<Fact>]
let ``doors, player start and NPC placement are bounds-checked`` () =
    let project = starter ()
    let broken = project |> apply (SetTransition("scene-farm", { SceneTransition.Default with FromX = 50.0; FromY = 1.0; ToSceneId = "scene-farm"; ToX = 1.0; ToY = 1.0 }))
    let problem = has "transition.fromOutOfBounds" broken
    Assert.Equal("scene", problem.TargetKind)
    let door = { SceneTransition.Default with FromX = 8.0; FromY = 11.0; ToSceneId = "scene-farm"; ToX = 1.0; ToY = 1.0 }
    let twoDoors = { (farm project) with Transitions = ([ door; door ]) }
    has "transition.duplicateFrom" ({ project with Scenes = ([ twoDoors ]) }) |> ignore
    let lost = { project with Player = { project.Player with X = 99.0 } }
    let start = has "player.startOutOfBounds" lost
    Assert.Equal("player.x", start.Path)
    has "player.sceneMissing" ({ project with Player = { project.Player with SceneId = "nowhere" } }) |> ignore
    let far = project |> apply (UpsertNpc{ Defaults.newNpc project "Far" with X = 99.0 })
    has "npc.outOfBounds" far |> ignore

[<Fact>]
let ``duplicate ids across every family`` () =
    let project = starter ()
    let dup = { project with Items = (project.Items @ [ project.Items.[0] ]) }
    let problem = has "duplicate.item" dup
    Assert.Equal(sprintf "items[%d].id" project.Items.Length, problem.Path)
    let dupAction = { project with Actions = (project.Actions @ [ project.Actions.[0] ]) }
    has "duplicate.action" dupAction |> ignore

[<Fact>]
let ``recipes, machines, quests and wildlife links`` () =
    let project = starter ()
    let recipe =
        { Defaults.newRecipe project with
            Inputs = [ { RecipeIngredient.Default with ItemId = "ghost"; Quantity = 1.0 } ]
            MachineTypeId = Some "machine-ghost"
            RequiresStationCategory = Some "alchemy" }
    let broken = project |> apply (UpsertRecipe recipe)
    has "recipe.unknownInput" broken |> ignore
    has "recipe.unknownMachine" broken |> ignore
    has "recipe.unknownStation" broken |> ignore
    has "recipe.noOutputs" broken |> ignore
    let machine = has "machineType.noItem" (project |> apply (UpsertMachineType(Defaults.newMachineType project)))
    Assert.Equal("machineType", machine.TargetKind)
    let quest = { Defaults.newQuest project with Giver = Some "npc-ghost"; Prerequisites = Some [ "quest-1" ] }
    let questBroken = project |> apply (UpsertQuest quest)
    has "quest.giverMissing" questBroken |> ignore
    has "quest.prerequisiteSelf" questBroken |> ignore
    let species = { Defaults.newAnimalSpecies project with ProductItemId = "ghost"; FeedItemId = Some "ghost-feed" }
    let wild = project |> apply (UpsertAnimalSpecies species)
    has "species.unknownProduct" wild |> ignore
    has "species.unknownFeed" wild |> ignore

[<Fact>]
let ``graphics, interface, calendar, mine and packs`` () =
    let project = starter ()
    let art = project |> apply (BindVisual(NpcVisual "npc-farmer", Some({ VisualRef.Default with AssetId = "missing-art" })))
    let missing = has "graphics.missingAsset" art
    Assert.Equal("npcs[0].visual.assetId", missing.Path)
    let panel = { (Defaults.newGamePanel project) with Entries = ([ { GamePanelEntry.Default with Label = "Use"; Kind = "action"; Value = "" } ]) }
    let ui = has "interface.missingAction" (project |> apply (SetGamePanels [ panel ]))
    Assert.Equal("interface", ui.TargetKind)
    let festival = { CalendarFestival.Default with Id = "festival-1"; Name = "Harvest"; SeasonId = "spring"; Day = 40.0 }
    let calendar = { project.Settings.Calendar with Festivals = ([ festival ]) }
    has "calendar.festivalDayOutOfRange" (project |> apply (SetSettings({ project.Settings with Calendar = calendar }))) |> ignore
    let mine = { Defaults.mineEnabled project true with EntranceSceneId = Some "scene-ghost" }
    let mined = has "mine.entranceSceneMissing" (project |> apply (SetMine mine))
    Assert.Equal("settings", mined.TargetKind)
    let pack = ProjectCatalog.CreateContentDefaultPack()
    let old = { pack with Manifest = { pack.Manifest with Id = "old-pack"; EngineCompatibility = ">=99.0.0" } }
    let packed = has "pack.incompatible" (blank () |> apply (InstallPack old))
    Assert.Equal("pack", packed.TargetKind)
    Assert.Equal("old-pack", packed.TargetId)

[<Fact>]
let ``reserved hotkeys and C# friendly members`` () =
    let project = starter ()
    let action = { (Defaults.newAction project) with Hotkey = Some "w" }
    let problem = has "action.reservedHotkey" (project |> apply (UpsertAction action))
    Assert.Equal("warning", problem.SeverityName)
    Assert.True problem.IsWarning
    Assert.True problem.HasTarget
    Assert.Equal("action", problem.TargetKind)
    Assert.Equal(action.Id, problem.TargetId)
    Assert.Equal(0, problem.TargetX)
    let tile: Problem = { Severity = Severity.Info; Code = "x"; Message = "m"; Path = "p"; Target = Some(NavigationTarget.Scene("scene-farm", 3, 4)) }
    Assert.Equal("scene", tile.TargetKind)
    Assert.Equal(3, tile.TargetX)
    Assert.Equal(4, tile.TargetY)
    let nowhere: Problem = { tile with Target = None }
    Assert.Null nowhere.TargetId

[<Fact>]
let ``actions and minigame tiers check the same references as events`` () =
    let project = starter ()
    let conditions: EventCondition list =
        [ EventCondition.HasItem { HasItemCondition.Default with ItemId = "ghost-item"; Quantity = 1.0 }
          EventCondition.InventorySpace { InventorySpaceCondition.Default with ItemId = "ghost-room"; Quantity = 1.0 }
          EventCondition.QuestStatus { QuestStatusCondition.Default with QuestId = "ghost-quest"; Status = "completed" } ]
    let outcomes =
        [ { EventOutcome.Default with Type = "giveItem"; ItemId = Some("ghost-item") }
          { EventOutcome.Default with Type = "startQuest"; QuestId = Some("ghost-quest") }
          { EventOutcome.Default with Type = "spawnNPC"; NpcId = Some("ghost-npc") }
          { EventOutcome.Default with Type = "warpPlayer"; SceneId = Some("ghost-scene") }
          { EventOutcome.Default with Type = "lockTransition"; SceneId = Some("ghost-scene"); X = Some(0.0); Y = Some(0.0) } ]
    let action = { Defaults.newAction project with Conditions = conditions; Outcomes = outcomes }
    let broken = project |> apply (UpsertAction action)
    let index = broken.Actions.Length - 1
    let at (code: string) = (has code broken).Path
    let paths code = Problems.collect broken |> List.filter (fun p -> p.Code = code) |> List.map (fun p -> p.Path)
    Assert.Equal<string list>([ sprintf "actions[%d].conditions[0].itemId" index; sprintf "actions[%d].conditions[1].itemId" index ], paths "action.conditionUnknownItem")
    Assert.Equal(sprintf "actions[%d].conditions[2].questId" index, at "action.conditionUnknownQuest")
    Assert.Equal(sprintf "actions[%d].outcomes[0].itemId" index, at "action.outcomeUnknownItem")
    Assert.Equal(sprintf "actions[%d].outcomes[1].questId" index, at "action.outcomeUnknownQuest")
    Assert.Equal(sprintf "actions[%d].outcomes[2].npcId" index, at "action.outcomeUnknownNpc")
    Assert.Equal(2, (paths "action.outcomeUnknownScene").Length)
    Assert.True((has "action.outcomeUnknownItem" broken).IsError)
    // Event outcomes keep their content lints (no double report), but gain the new checks.
    let event = { Defaults.newEvent project with Conditions = conditions; Outcomes = outcomes }
    let brokenEvent = project |> apply (UpsertEvent event)
    lacks "event.outcomeUnknownItem" brokenEvent
    lacks "event.conditionUnknownQuest" brokenEvent
    has "event.conditionUnknownItem" brokenEvent |> ignore
    has "event.outcomeUnknownScene" brokenEvent |> ignore
    let tier = { MinigameResultTier.Default with MinScore = 0.0; Outcomes = [ { EventOutcome.Default with Type = "takeItem"; ItemId = Some("ghost-item") } ] }
    let minigame = { (Defaults.newMinigame project) with ResultTiers = ([ tier ]) }
    let tiered = project |> apply (UpsertMinigame minigame)
    Assert.Equal(sprintf "minigames[%d].resultTiers[0].outcomes[0].itemId" (tiered.Minigames.Length - 1), (has "minigame.outcomeUnknownItem" tiered).Path)

[<Fact>]
let ``dialogue options, gift tastes and item crops name things that exist`` () =
    let project = starter ()
    let farmer = npc project "npc-farmer"
    let greeting = farmer.Dialogue.[0]
    let option = { DialogueOption.Default with Text = "Trade?"; RequiresItem = Some("ghost-item"); ActionId = Some("ghost-action") }
    let dialogue = { greeting with Options = (greeting.Options @ [ option ]) }
    let broken = project |> apply (UpsertDialogue dialogue)
    let index = greeting.Options.Length
    Assert.Equal(sprintf "npcs[0].dialogue[0].options[%d].requiresItem" index, (has "dialogue.optionUnknownItem" broken).Path)
    let action = has "dialogue.optionUnknownAction" broken
    Assert.Equal(sprintf "npcs[0].dialogue[0].options[%d].actionId" index, action.Path)
    Assert.Equal("npc", action.TargetKind)
    let tastes = { GiftTastes.Default with Loved = [ project.Items.[0].Id; "ghost-gift" ] }
    let fussy = project |> apply (UpsertNpc({ farmer with GiftTastes = Some tastes }))
    let gift = has "npc.giftUnknownItem" fussy
    Assert.Equal("npcs[0].giftTastes.loved[1]", gift.Path)
    Assert.True gift.IsWarning
    let odd = { (Defaults.newItem project) with CropType = Some "ghost-crop" }
    let crop = has "item.unknownCrop" (project |> apply (UpsertItem odd))
    Assert.EndsWith(".cropType", crop.Path)

[<Fact>]
let ``placed machines, scene lists, player quests and recipe skills are checked`` () =
    let project = starter ()
    let farmScene = farm project
    let machine = { TileMachine.Default with TypeId = "machine-ghost"; Processing = Some({ MachineProcessing.Default with RecipeId = "recipe-ghost"; CompletesAtMinute = 0.0 }) }
    let tiles =
        farmScene.Tiles
        |> List.mapi (fun y row -> row |> List.mapi (fun x tile -> if (x, y) = (3, 2) then { tile with Machine = Some machine } else tile))
    let scene = { farmScene with Tiles = tiles; Npcs = [ "npc-ghost" ]; Events = [ "event-ghost" ] }
    let scenes = project.Scenes |> List.map (fun s -> if s.Id = scene.Id then scene else s)
    let broken = { project with Scenes = scenes }
    let machine = has "scene.machineUnknownType" broken
    Assert.EndsWith("tiles[2][3].machine.typeId", machine.Path)
    Assert.Equal(("scene", 3, 2), (machine.TargetKind, machine.TargetX, machine.TargetY))
    has "scene.machineUnknownRecipe" broken |> ignore
    has "scene.unknownNpc" broken |> ignore
    has "scene.unknownEvent" broken |> ignore
    let player = { project.Player with ActiveQuests = ([ "quest-ghost" ]) }
    Assert.Equal("player.activeQuests[0]", (has "player.unknownQuest" ({ project with Player = player })).Path)
    let unlock = { RecipeUnlock.Default with Skill = Some({ RecipeSkillRequirement.Default with Skill = "juggling"; Level = 1.0 }) }
    let recipe = { (Defaults.newRecipe project) with Unlock = Some unlock }
    Assert.EndsWith(".unlock.skill.skill", (has "recipe.unknownSkill" (project |> apply (UpsertRecipe recipe))).Path)
