/// The validator unit tests (`SchemaChecks`, `ContentLints`), first written for the C# port:
/// constraint violations and what zod accepts, migrated fixtures validate, project validation,
/// pack problems and templates with zero content problems.
module FarmEngine.Authoring.Tests.ValidationTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private fixture (name: string) = Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private v8Json () = JsonNode.Parse(File.ReadAllText(fixture "project-v8.json")).AsObject()

/// The typed parse of project JSON (must succeed).
let private parse (json: JsonNode) : GameProject =
    match Decode.run SchemaJson.decodeGameProject (JsonInterop.ofNode json) with
    | Ok project -> project
    | Error issue -> failwith issue

let private toNode (project: GameProject) : JsonNode = ProjectMigrations.toNode project

let private startsWith (prefix: string) (entries: string list) =
    entries |> List.exists (fun e -> e.StartsWith(prefix, StringComparison.Ordinal))

// ── SchemaRoundTripTests ─────────────────────────────────────────────────────

[<Fact>]
let ``the v8 fixture validates`` () =
    Assert.Empty(SchemaChecks.validateProject (parse (v8Json ())))

[<Fact>]
let ``native export settings survive the web compatible import path`` () =
    let project = starter ()
    let settings = Defaults.newExportSettings project
    let project = project |> apply (SetExportSettings(Some settings))
    let result = ProjectMigrations.migrateProject (toNode project)
    Assert.True(result.Ok, String.concat "\n" result.Errors)
    Assert.Equal(settings.GameId, result.Data.Value.Export.Value.GameId)
    Assert.Equal(settings.ExecutableName, result.Data.Value.Export.Value.ExecutableName)

[<Fact>]
let ``F sharp compiler reproduces the checked-in cartridge and blocks errors`` () =
    let imported = ProjectMigrations.migrateProject (v8Json ())
    Assert.True imported.Ok
    let compiled = CartridgeCompiler.Compile imported.Data.Value
    // After an intended format or content change: FARM_RECORD_CARTRIDGES=1 rewrites the file.
    if Environment.GetEnvironmentVariable "FARM_RECORD_CARTRIDGES" = "1" then
        File.WriteAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixtures", "golden", "cartridges", "project-v8.cart"), compiled)
    else
        Assert.Equal<byte>(File.ReadAllBytes(fixture "project-v8.cart"), compiled)
    let broken = { imported.Data.Value with SelectedTileType = "lava" }
    Assert.Throws<InvalidOperationException>(fun () -> CartridgeCompiler.Compile broken |> ignore) |> ignore

[<Fact>]
let ``validateProject reports constraint violations`` () =
    let json = v8Json ()
    json.["mode"] <- JsonValue.Create "bogus"
    let scene = json.["scenes"].[0].DeepClone().AsObject()
    let tile = scene.["tiles"].[0].[0].DeepClone().AsObject()
    tile.["background"] <- JsonValue.Create "lava"
    scene.["width"] <- JsonValue.Create 3
    scene.["height"] <- JsonValue.Create 1.5
    scene.["tiles"] <- JsonNode.Parse(sprintf "[[%s]]" (tile.ToJsonString()))
    json.["scenes"] <- JsonNode.Parse(sprintf "[%s]" (scene.ToJsonString()))
    json.["quests"] <- JsonNode.Parse """[{"id":"q","name":"Q","description":"","status":"pending","objectives":[],"rewards":{}}]"""
    json.["events"] <-
        JsonNode.Parse
            """[{"id":"","name":"E","trigger":"enter","conditions":[{"type":"inventorySpace","itemId":"a","quantity":0},{"type":"timeOfDay","minMinute":800,"maxMinute":300}],"outcomes":[{"type":"waterArea","radius":11}]}]"""
    let broken = parse json
    // Parse-level: what zod rejects.
    let errors = SchemaChecks.validateProject broken
    for prefix in [ "mode:"; "scenes.0.height:"; "scenes.0.tiles.0.0.background:"; "quests.0.status:"; "events.0.conditions.0.quantity:"; "events.0.outcomes.0.radius:" ] do
        Assert.True(startsWith prefix errors, prefix)
    // Lint-level: what the web editor lets a creator save. Never a parse error.
    for prefix in [ "scenes.0.tiles.0:"; "events.0.id:"; "events.0.conditions.1:" ] do
        Assert.False(startsWith prefix errors, prefix)
    let lint = SchemaChecks.lintProject broken
    for prefix in [ "scenes.0.tiles.0:"; "events.0.id:" ] do
        Assert.True(startsWith prefix lint, prefix)
    // 800–300 is a time of day range that wraps past midnight, as the engine reads it.
    Assert.False(startsWith "events.0.conditions.1:" lint)
    Assert.False(startsWith "mode:" lint)

/// Shapes the web editor produces and zod accepts (no refinements exist for them) must load
/// here: inverted regions, inverted day ranges, a start scene that was deleted, an empty id.
/// Import parity, not lint cleanliness.
[<Fact>]
let ``validateProject accepts what zod accepts`` () =
    let json = v8Json ()
    json.["startSceneId"] <- JsonValue.Create "scene-that-was-deleted"
    json.["items"].AsArray().Add(
        JsonNode.Parse """{"id":"","name":"Unnamed","description":"","type":"material","stackable":true,"maxStack":1,"value":0}""")
    json.["events"] <-
        JsonNode.Parse
            """[{"id":"e","name":"E","trigger":"enter","conditions":[{"type":"enterTile","x":5,"y":5,"x2":2,"y2":1},{"type":"dayRange","minDay":20,"maxDay":3},{"type":"timeOfDay","minMinute":800,"maxMinute":300}],"outcomes":[]}]"""
    let webShaped = parse json
    Assert.Empty(SchemaChecks.validateProject webShaped)
    Assert.True((ProjectMigrations.migrateProject (toNode webShaped)).Ok)
    Assert.NotEmpty(SchemaChecks.lintProject webShaped)

// ── MigrationTests ───────────────────────────────────────────────────────────

let migrationFixtures () : seq<obj[]> = [ 1..8 ] |> Seq.map (fun v -> [| box (sprintf "project-v%d.json" v) |])

[<Theory>]
[<MemberData(nameof migrationFixtures)>]
let ``migrates a fixture to the current schema version and validates`` (name: string) =
    let result = ProjectMigrations.migrateProject (JsonNode.Parse(File.ReadAllText(fixture name)))
    Assert.Empty result.Errors
    Assert.True result.Ok
    Assert.Equal(ProjectSchema.CurrentProjectSchemaVersion, result.Data.Value.SchemaVersion)
    Assert.Empty(SchemaChecks.validateProject result.Data.Value)
    // Re-parsing the output succeeds (GameProjectSchema.safeParse(result.data)).
    let reparsed = parse (toNode result.Data.Value)
    Assert.Equal(result.Data.Value, reparsed)
    Assert.Empty(SchemaChecks.validateProject reparsed)

// ── M3SystemsTests: project validation (Problems panel) ──────────────────────

/// `EngineTests.MakeProject`: a small healthy project.
let private makeProject () : GameProject =
    let scene = AuthoringTiles.CreateEmptyScene("scene-test", "Test Farm", 6.0, 6.0)
    let scene =
        scene
        |> mapTile 3 2 (fun t -> AuthoringTiles.SetTileLayer(t, "soil"))
        |> mapTile 4 4 (fun t -> AuthoringTiles.SetTileLayer(t, "wall"))
    let items = Builtin.items ()
    let slot id quantity : InventorySlot = { Item = (items |> List.find (fun i -> i.Id = id)); Quantity = quantity }
    let dialogue : Dialogue list =
        [ { Dialogue.Default with
              Id = "dlg-1"; NpcId = "npc-test"; Text = "Hello!"
              Options =
                [ { DialogueOption.Default with Text = "Bye" }
                  { DialogueOption.Default with Text = "Gift me"; GiveMoney = Some 25.0; NextDialogueId = Some "dlg-2" } ] }
          { Dialogue.Default with Id = "dlg-2"; NpcId = "npc-test"; Text = "More?"; Options = [ { DialogueOption.Default with Text = "No" } ] } ]
    let npc =
        { Npc.Default with
            Id = "npc-test"; Name = "Testy"; X = 1.0; Y = 1.0; SceneId = "scene-test"; Dialogue = dialogue; CanMove = false; Appearance = "farmer" }
    let quest =
        { Quest.Default with
            Id = "quest-wheat"; Name = "Wheat!"; Description = "Harvest 1 wheat"; Status = "active"
            Objectives =
                [ { QuestObjective.Default with
                      Id = "obj-1"; Type = "harvest"; Description = "Harvest wheat"; TargetCropType = Some "wheat"
                      TargetCropQuantity = Some 1.0; Completed = false; Progress = 0.0 } ]
            Rewards = { QuestRewards.Default with Money = Some 100.0 } }
    let sunOnly () = [ ({ WeatherId = "sun"; Weight = 1.0 } : WeatherTableEntry) ]
    { GameProject.Default with
        SchemaVersion = 4.0
        Id = "proj-test"
        Name = "Test"
        Version = "2.0"
        Scenes = [ scene ]
        Npcs = [ npc ]
        Items = items
        Events = []
        Dialogues = dialogue
        Quests = [ quest ]
        Player =
            { Player.Default with
                X = 3.0; Y = 4.0; Direction = "up"; SceneId = "scene-test"
                Inventory = [ slot "seed-wheat" 5.0; slot "tool-hoe" 1.0; slot "tool-watering-can" 1.0 ]
                MaxInventorySize = 10.0; Money = 100.0; ActiveQuests = [ "quest-wheat" ] }
        StartSceneId = "scene-test"
        Mode = "play"
        SelectedTileType = "grass"
        CurrentTime = 1_000_000.0
        CurrentSeason = "spring"
        CurrentDay = 1.0
        CurrentTimeMinutes = 360.0
        CurrentYear = 1.0
        Settings = SettingsSchema.DefaultProjectSettings
        Weather =
            { WeatherConfig.Default with
                Types = [ { WeatherTypeDefinition.Default with Id = "sun"; Name = "Sunny" } ]
                Table = [ for season in [ "spring"; "summer"; "fall"; "winter" ] -> season, sunOnly () ] }
        Mine = { MineConfig.Default with Enabled = false }
        GameStartTime = 1_000_000.0 }

[<Fact>]
let ``a healthy starter project has no errors`` () =
    for project in [ makeProject (); starter () ] do
        let problems = ContentLints.validateProjectContent project
        Assert.DoesNotContain(problems, fun p -> p.Severity = Severity.Error)
        Assert.Empty(SchemaChecks.validateProject project)

[<Fact>]
let ``catches dangling references across content families`` () =
    let project = makeProject ()
    let scene = { project.Scenes.Head with Transitions = [ { SceneTransition.Default with ToSceneId = "nope" } ] }
    // The NPC's dialogue and the project list hold the same dialogue (TS mutates the same object).
    let dialogue = project.Npcs.Head.Dialogue.Head
    let dialogue = { dialogue with Options = [ dialogue.Options.Head; { dialogue.Options.[1] with NextDialogueId = Some "dlg-missing" } ] }
    let quest = project.Quests.Head
    let quest = { quest with Rewards = { quest.Rewards with Items = Some [ { ItemId = "item-missing"; Quantity = 1.0 } ] } }
    let shop =
        { ShopDefinition.Default with
            Id = "s"; Name = "S"; Stock = [ { ShopStockEntry.Default with ItemId = "ghost" } ]; SellPriceMultiplier = 1.0
            BuysItems = true; RepairsTools = false; RepairCostPerPoint = 0.5 }
    let event =
        { GameEvent.Default with
            Id = "e"; Name = "E"; SceneId = "scene-missing-2"; Trigger = "enter"
            Conditions = [ EventCondition.HasItem { ItemId = "no-item"; Quantity = 1.0 } ]
            Outcomes = [ { EventOutcome.Default with Type = "startQuest"; QuestId = Some "no-quest" } ]
            Active = true; Repeatable = false }
    let project =
        { project with
            Scenes = [ scene ]
            Npcs = [ { project.Npcs.Head with Dialogue = dialogue :: project.Npcs.Head.Dialogue.Tail } ]
            Dialogues = dialogue :: project.Dialogues.Tail
            Quests = [ quest ]
            StartSceneId = "scene-missing"
            Shops = [ shop ]
            Events = [ event ] }
    let problems = ContentLints.validateProjectContent project
    let categories = problems |> List.map (fun p -> p.Category) |> set
    for category in [ "scenes"; "transitions"; "dialogue"; "quests"; "shops"; "events" ] do
        Assert.Contains(category, categories)
    Assert.True(problems.Length >= 7)

[<Fact>]
let ``flags unreachable scenes as warnings`` () =
    let project = makeProject ()
    let project = { project with Scenes = project.Scenes @ [ AuthoringTiles.CreateEmptyScene("scene-island", "Island", 4.0, 4.0) ] }
    let problems = ContentLints.validateProjectContent project
    Assert.Contains(problems, fun p -> p.Severity = Severity.Warning && p.Message.Contains "Island")

// ── M5SystemsTests: packs in the Problems panel ──────────────────────────────

[<Fact>]
let ``surfaces pack problems in the problems panel validation`` () =
    let raw =
        { ContentPack.Default with
            Manifest = { PackManifest.Default with Id = "needy"; Name = "needy"; Version = "1.0.0"; Dependencies = [ { PackId = "nope"; Version = None } ] } }
    let pack =
        match PackRules.validateContentPack (SchemaJson.encodeContentPack raw) with
        | Ok pack -> pack
        | Error errors -> failwith (String.concat "; " errors)
    let project = { (makeProject ()) with ContentPacks = [ { Pack = pack; Enabled = true } ] }
    let problems = ContentLints.validateProjectContent project
    Assert.Contains(problems, fun p -> p.Category = "packs" && p.Message.Contains "'nope'" && p.Subject = Some "needy")

// ── PolishAndEcosystemTests: sample templates ────────────────────────────────

let templateFactories () : seq<obj[]> = [ [| box "cozy" |]; [| box "quest" |] ]

[<Theory>]
[<MemberData(nameof templateFactories)>]
let ``template validates and has zero content problems`` (name: string) =
    let project = if name = "cozy" then ProjectCatalog.CreateCozyFarmProject(0.0) else ProjectCatalog.CreateQuestRpgProject(0.0)
    let result = ProjectMigrations.migrateProject (toNode project)
    Assert.True(result.Ok, String.Join("; ", result.Errors))
    Assert.Empty(ContentLints.validateProjectContent project)

// ── F#-only ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``schema issues carry the path and message the string form joins`` () =
    let project = parse (v8Json ())
    let broken = { project with Mode = "bogus"; CurrentYear = 1.5 }
    let issues = SchemaChecks.projectIssues broken
    Assert.Equal<string list>([ "mode"; "currentYear" ], issues |> List.map (fun i -> i.Path))
    Assert.Equal<string list>(SchemaChecks.validateProject broken, issues |> List.map string)
    Assert.Equal("currentYear: Expected integer, received 1.5", string issues.[1])

[<Fact>]
let ``exported games skip the editor-only fields and an absent player`` () =
    let json = BrokenProjects.starterJson ()
    json.["player"].["direction"] <- JsonValue.Create "north"
    let game (json: JsonNode) =
        match Decode.run SchemaJson.decodeExportedGame (JsonInterop.ofNode json) with
        | Ok game -> game
        | Error issue -> failwith issue
    Assert.Equal<string list>([ "player.direction: Invalid enum value. Expected 'up' | 'down' | 'left' | 'right', received 'north'" ], SchemaChecks.validateExportedGame (game json))
    json.Remove "player" |> ignore
    Assert.Empty(SchemaChecks.validateExportedGame (game json))
