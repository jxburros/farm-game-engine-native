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

[<Fact>]
let ``a day that ends before it starts, too late or too fast is an error`` () =
    let project = starter ()
    let withTime (time: TimeConfig) = project |> apply (SetSettings({ project.Settings with Time = time }))
    let inverted = withTime { project.Settings.Time with DayStartMinute = 1560.0; DayEndMinute = 1500.0 }
    let problem = has "schema.settings" inverted
    Assert.Equal("settings.time.dayEndMinute", problem.Path)
    Assert.True problem.IsError
    Assert.True(Problems.blocksExport (Problems.collect inverted))
    has "schema.settings" (withTime { project.Settings.Time with DayStartMinute = 600.0; DayEndMinute = 630.0 }) |> ignore
    has "schema.settings" (withTime { project.Settings.Time with DayEndMinute = 4295.0 }) |> ignore
    has "schema.settings" (withTime { project.Settings.Time with MinutesPerRealSecond = 1441.0 }) |> ignore
    lacks "schema.settings" (withTime { project.Settings.Time with DayEndMinute = 4294.0; MinutesPerRealSecond = 1440.0 })

[<Fact>]
let ``time of day ranges may wrap past midnight`` () =
    let project = starter ()
    let condition = EventCondition.TimeOfDay { MinMinute = 22.0 * 60.0; MaxMinute = 2.0 * 60.0 }
    let event = { Defaults.newEvent project with Conditions = [ condition ] }
    let withEvent = project |> apply (UpsertEvent event)
    Assert.Empty(Problems.collect withEvent |> List.filter (fun p -> p.Message.Contains "minMinute"))

[<Fact>]
let ``a starting day outside the starting season says which day the game starts on`` () =
    let project = starter ()
    // Absolute day 1 is spring; the game starts in summer (day 1 of summer).
    let summer = { project with CurrentSeason = "summer"; CurrentDay = 1.0 }
    let problem = has "project.currentDayOutsideSeason" summer
    Assert.False problem.IsError
    Assert.Contains("day 1 of Summer", problem.Message)
    lacks "project.currentDayOutsideSeason" project
    // Keep changes records the day of season: nothing to warn about then.
    lacks "project.currentDayOutsideSeason" { summer with CurrentDayOfSeason = Some 12.0 }
    has "project.currentDayOfSeasonOutOfRange" { summer with CurrentDayOfSeason = Some 40.0 } |> ignore

[<Fact>]
let ``scene grids, sizes and scene ids the game would have to fix are errors`` () =
    let project = starter ()
    let scene = farm project
    let index = project.Scenes |> List.findIndex (fun s -> s.Id = scene.Id)
    let withFarm (edited: Scene) = { project with Scenes = project.Scenes |> List.map (fun s -> if s.Id = scene.Id then edited else s) }
    for code in [ "scene.gridWidth"; "scene.gridRows"; "scene.tooLarge"; "duplicate.scene" ] do
        lacks code project
    let ragged = withFarm { scene with Tiles = scene.Tiles |> List.mapi (fun y row -> if y = 3 then List.truncate 3 row else row) }
    let width = has "scene.gridWidth" ragged
    Assert.Equal(sprintf "scenes[%d].tiles[3]" index, width.Path)
    Assert.True width.IsError
    Assert.Equal(sprintf "scenes[%d].tiles" index, (has "scene.gridRows" (withFarm { scene with Tiles = List.truncate 5 scene.Tiles })).Path)
    Assert.True((has "scene.tooLarge" (withFarm { scene with Width = 300.0 })).IsError)
    Assert.True((has "duplicate.scene" { project with Scenes = project.Scenes @ [ scene ] }).IsError)
    Assert.True(Problems.blocksExport (Problems.collect ragged))

[<Fact>]
let ``mine floors outside 3 to 256 tiles a side are errors`` () =
    let project = starter ()
    let mine = Defaults.mineEnabled project true
    lacks "mine.floorSize" (project |> apply (SetMine mine))
    let huge = has "mine.floorSize" (project |> apply (SetMine { mine with FloorWidth = 50000.0 }))
    Assert.Equal("mine.floorWidth", huge.Path)
    Assert.True huge.IsError
    Assert.Equal("mine.floorHeight", (has "mine.floorSize" (project |> apply (SetMine { mine with FloorHeight = 2.0 }))).Path)

[<Fact>]
let ``a player speed too fast to control is a warning`` () =
    let project = starter ()
    lacks "settings.playerSpeedFast" project
    let settings = { project.Settings with Movement = { project.Settings.Movement with PlayerSpeed = 60.0 } }
    let fast = has "settings.playerSpeedFast" (project |> apply (SetSettings settings))
    Assert.True fast.IsWarning
    Assert.Equal("settings.movement.playerSpeed", fast.Path)

[<Fact>]
let ``doors, warps and the player start that land on blocked tiles are warnings`` () =
    let project = starter ()
    let scene = farm project
    // The starter farm has a wall border.
    Assert.True((List.head (List.head scene.Tiles)).Collision)
    let door = { SceneTransition.Default with FromX = 8.0; FromY = 11.0; ToSceneId = scene.Id; ToX = 0.0; ToY = 0.0 }
    let blocked = has "transition.landsBlocked" (project |> apply (SetTransition(scene.Id, door)))
    Assert.True blocked.IsWarning
    let warp = { Defaults.newAction project with Outcomes = [ { EventOutcome.Default with Type = "warpPlayer"; SceneId = Some scene.Id; X = Some 0.0; Y = Some 0.0 } ] }
    Assert.True((has "action.outcomeWarpBlocked" (project |> apply (UpsertAction warp))).IsWarning)
    let walled = { project with Player = { project.Player with SceneId = scene.Id; X = 0.0; Y = 0.0 } }
    Assert.True((has "player.startBlocked" walled).IsWarning)
    lacks "player.startBlocked" project

[<Fact>]
let ``actions that perform each other in a loop are warnings`` () =
    let project = starter ()
    let perform (id: string) = { EventOutcome.Default with Type = "performAction"; ActionId = Some id }
    let a = { Defaults.newAction project with Id = "action-a"; Name = "A"; Outcomes = [ perform "action-b" ] }
    let b = { Defaults.newAction project with Id = "action-b"; Name = "B"; Outcomes = [ perform "action-a" ] }
    let c = { Defaults.newAction project with Id = "action-c"; Name = "C"; Outcomes = [ perform "action-a" ] }
    let looping = project |> apply (UpsertAction a) |> apply (UpsertAction b) |> apply (UpsertAction c)
    let cycles = Problems.collect looping |> List.filter (fun p -> p.Code = "action.cycle")
    Assert.Equal<string list>([ "action-a"; "action-b" ], cycles |> List.map (fun p -> p.TargetId))
    Assert.True(cycles |> List.forall (fun p -> p.IsWarning))
    lacks "action.cycle" (project |> apply (UpsertAction c))

[<Fact>]
let ``pack warps are checked against the project's and the packs' scenes`` () =
    let cave = { Scene.Default with Id = "cave"; Name = "Cave"; Width = 4.0; Height = 3.0 }
    let warp (sceneId: string) (x: float) = { EventOutcome.Default with Type = "warpPlayer"; SceneId = Some sceneId; X = Some x; Y = Some 1.0 }
    let action (id: string) outcome = { ActionDef.Default with Id = id; Name = id; Outcomes = [ outcome ] }
    let pack =
        { ContentPack.Default with
            Manifest = { PackManifest.Default with Id = "caves"; Name = "Caves"; Version = "1.0.0" }
            Content =
                { PackContent.Default with
                    Scenes = [ cave ]
                    Actions = [ action "local" (warp "cave" 1.0); action "far" (warp "caves:cave" 9.0); action "fine" (warp "caves:cave" 1.0) ] } }
    let packed = blank () |> apply (InstallPack pack)
    let unknown = has "pack.warpUnknownScene" packed
    Assert.Contains("write \"caves:cave\"", unknown.Message)
    Assert.Equal("pack", unknown.TargetKind)
    Assert.Equal("contentPacks[0].pack.content.actions[1].outcomes[0].x", (has "pack.warpOutOfBounds" packed).Path)
    Assert.Equal(2, Problems.collect packed |> List.filter (fun p -> p.Code.StartsWith "pack.warp") |> List.length)

[<Fact>]
let ``crops without a harvest item and repeatable dialogue rewards are reported`` () =
    let project = starter ()
    let crop = { (Defaults.newCrop project) with Id = "glowberry"; Name = "Glowberry" }
    let planted = project |> apply (UpsertCrop crop)
    lacks "crop.noHarvestItem" planted
    let noProduce = planted |> apply (RemoveItem "crop-glowberry")
    let missing = has "crop.noHarvestItem" noProduce
    Assert.True missing.IsWarning
    Assert.EndsWith(".harvestItemId", missing.Path)
    // Naming another item fixes it; naming a missing one is an error.
    let named = { crop with HarvestItemId = Some "material-fiber" }
    lacks "crop.noHarvestItem" (noProduce |> apply (UpsertCrop named))
    Assert.True (has "crop.noHarvestItem" (noProduce |> apply (UpsertCrop { crop with HarvestItemId = Some "ghost" }))).IsError

    let farmer = npc project "npc-farmer"
    let greeting = farmer.Dialogue.[0]
    let gift = { DialogueOption.Default with Text = "Gift me"; GiveMoney = Some 25.0; Once = Some false }
    let generous = project |> apply (UpsertDialogue { greeting with Options = greeting.Options @ [ gift ] })
    let warning = has "dialogue.repeatableReward" generous
    Assert.Equal(sprintf "npcs[0].dialogue[0].options[%d].once" greeting.Options.Length, warning.Path)
    for guarded in [ { gift with Once = Some true }; { gift with Once = None; HiddenIfFlag = Some "gifted" }; { gift with TakeMoney = Some 5.0 } ] do
        lacks "dialogue.repeatableReward" (project |> apply (UpsertDialogue { greeting with Options = greeting.Options @ [ guarded ] }))

[<Fact>]
let ``quest experience names a known skill`` () =
    let project = starter ()
    let quest = project.Quests.[0]
    let odd = { quest with Rewards = { quest.Rewards with Experience = Some 10.0; Skill = Some "cooking" } }
    Assert.EndsWith(".rewards.skill", (has "quest.unknownSkill" (project |> apply (UpsertQuest odd))).Path)
    lacks "quest.unknownSkill" (project |> apply (UpsertQuest { odd with Rewards = { odd.Rewards with Skill = Some "mining" } }))
