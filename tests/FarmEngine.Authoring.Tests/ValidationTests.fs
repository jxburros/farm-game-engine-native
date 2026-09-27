/// The C# validator unit tests, ported to the F# `SchemaChecks` and `ContentLints`:
/// SchemaRoundTripTests (constraint violations, what zod accepts), MigrationTests (migrated
/// fixtures validate), M3SystemsTests (project validation), M5SystemsTests (pack problems) and
/// PolishAndEcosystemTests (templates have zero content problems).
module FarmEngine.Authoring.Tests.ValidationTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Content
open FarmEngine.Core
open FarmEngine.Json
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private fixture (name: string) = Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

let private v8Json () = JsonNode.Parse(File.ReadAllText(fixture "project-v8.json")).AsObject()

let private parse (json: JsonNode) = json.Deserialize<GameProject>(JsonDefaults.Options)

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
    let result = ProjectMigrations.migrateProject (JsonSerializer.SerializeToNode(project, JsonDefaults.Options))
    Assert.True(result.Ok, String.concat "\n" result.Errors)
    Assert.Equal(settings.GameId, result.Data.Export.GameId)
    Assert.Equal(settings.ExecutableName, result.Data.Export.ExecutableName)

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
    for prefix in [ "scenes.0.tiles.0:"; "events.0.id:"; "events.0.conditions.1:" ] do
        Assert.True(startsWith prefix lint, prefix)
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
    Assert.True((ProjectMigrations.migrateProject (JsonSerializer.SerializeToNode(webShaped, JsonDefaults.Options))).Ok)
    Assert.NotEmpty(SchemaChecks.lintProject webShaped)

// ── MigrationTests ───────────────────────────────────────────────────────────

let migrationFixtures () : seq<obj[]> = [ 1..8 ] |> Seq.map (fun v -> [| box (sprintf "project-v%d.json" v) |])

[<Theory>]
[<MemberData(nameof migrationFixtures)>]
let ``migrates a fixture to the current schema version and validates`` (name: string) =
    let result = ProjectMigrations.migrateProject (JsonNode.Parse(File.ReadAllText(fixture name)))
    Assert.Empty result.Errors
    Assert.True result.Ok
    Assert.Equal(ProjectSchema.CurrentProjectSchemaVersion, result.Data.SchemaVersion)
    Assert.Empty(SchemaChecks.validateProject result.Data)
    // Re-parsing the output succeeds (GameProjectSchema.safeParse(result.data)).
    let reparsed = JsonDefaults.Deserialize<GameProject>(JsonDefaults.Serialize result.Data)
    Assert.NotNull reparsed
    Assert.Empty(SchemaChecks.validateProject reparsed)

// ── M3SystemsTests: project validation (Problems panel) ──────────────────────

/// `EngineTests.MakeProject`: a small healthy project.
let private makeProject () : GameProject =
    let scene = Tiles.CreateEmptyScene("scene-test", "Test Farm", 6.0, 6.0)
    scene.Tiles.[2].[3] <- Tiles.SetTileLayer(scene.Tiles.[2].[3], "soil")
    scene.Tiles.[4].[4] <- Tiles.SetTileLayer(scene.Tiles.[4].[4], "wall")
    let items = ContentBuiltin.CreateDefaultItems()
    let slot id quantity = InventorySlot(Item = (items |> Seq.find (fun i -> i.Id = id)), Quantity = quantity)
    let dialogue =
        listOf
            [ Dialogue(
                  Id = "dlg-1",
                  NpcId = "npc-test",
                  Text = "Hello!",
                  Options = listOf [ DialogueOption(Text = "Bye"); DialogueOption(Text = "Gift me", GiveMoney = Nullable 25.0, NextDialogueId = "dlg-2") ]
              )
              Dialogue(Id = "dlg-2", NpcId = "npc-test", Text = "More?", Options = listOf [ DialogueOption(Text = "No") ]) ]
    let npc = Npc(Id = "npc-test", Name = "Testy", X = 1.0, Y = 1.0, SceneId = "scene-test", Dialogue = dialogue, CanMove = false, Appearance = "farmer")
    let quest =
        Quest(
            Id = "quest-wheat",
            Name = "Wheat!",
            Description = "Harvest 1 wheat",
            Status = "active",
            Objectives =
                listOf
                    [ QuestObjective(
                          Id = "obj-1",
                          Type = "harvest",
                          Description = "Harvest wheat",
                          TargetCropType = "wheat",
                          TargetCropQuantity = Nullable 1.0,
                          Completed = false,
                          Progress = 0.0
                      ) ],
            Rewards = QuestRewards(Money = Nullable 100.0)
        )
    let sunOnly () = listOf [ WeatherTableEntry(WeatherId = "sun", Weight = 1.0) ]
    let table = Collections.Generic.OrderedDictionary<string, Collections.Generic.List<WeatherTableEntry>>()
    for season in [ "spring"; "summer"; "fall"; "winter" ] do
        table.[season] <- sunOnly ()
    GameProject(
        SchemaVersion = 4.0,
        Id = "proj-test",
        Name = "Test",
        Version = "2.0",
        Scenes = listOf [ scene ],
        Npcs = listOf [ npc ],
        Items = items,
        Events = listOf [],
        // Same list instance as the NPC's dialogue (TS `dialogues: npc.dialogue`).
        Dialogues = dialogue,
        Quests = listOf [ quest ],
        Player =
            Player(
                X = 3.0,
                Y = 4.0,
                Direction = "up",
                SceneId = "scene-test",
                Inventory = listOf [ slot "seed-wheat" 5.0; slot "tool-hoe" 1.0; slot "tool-watering-can" 1.0 ],
                MaxInventorySize = 10.0,
                Money = 100.0,
                ActiveQuests = listOf [ "quest-wheat" ]
            ),
        StartSceneId = "scene-test",
        Mode = "play",
        SelectedTileType = "grass",
        CurrentTime = 1_000_000.0,
        CurrentSeason = "spring",
        CurrentDay = 1.0,
        CurrentTimeMinutes = 360.0,
        CurrentYear = 1.0,
        Settings = SettingsSchema.DefaultProjectSettings,
        Weather = WeatherConfig(Types = listOf [ WeatherTypeDefinition(Id = "sun", Name = "Sunny") ], Table = table),
        Mine = MineConfig(Enabled = false),
        GameStartTime = 1_000_000.0
    )

[<Fact>]
let ``a healthy starter project has no errors`` () =
    for project in [ makeProject (); starter () ] do
        let problems = ContentLints.validateProjectContent project
        Assert.DoesNotContain(problems, fun p -> p.Severity = Severity.Error)
        Assert.Empty(SchemaChecks.validateProject project)

[<Fact>]
let ``catches dangling references across content families`` () =
    let project = makeProject ()
    project.Scenes.[0] <-
        Records.withValue project.Scenes.[0] "Transitions" (box (listOf [ SceneTransition(FromX = 0.0, FromY = 0.0, ToSceneId = "nope", ToX = 0.0, ToY = 0.0) ]))
    // Options list is shared with project.Dialogues (TS mutates the same object).
    let options = project.Npcs.[0].Dialogue.[0].Options
    options.[1] <- Records.withValue options.[1] "NextDialogueId" (box "dlg-missing")
    project.Quests.[0] <-
        Records.withValue project.Quests.[0] "Rewards"
            (box (Records.withValue project.Quests.[0].Rewards "Items" (box (listOf [ QuestRewardItem(ItemId = "item-missing", Quantity = 1.0) ]))))
    let shop =
        ShopDefinition(
            Id = "s",
            Name = "S",
            Stock = listOf [ ShopStockEntry(ItemId = "ghost") ],
            SellPriceMultiplier = 1.0,
            BuysItems = true,
            RepairsTools = false,
            RepairCostPerPoint = 0.5
        )
    let event =
        GameEvent(
            Id = "e",
            Name = "E",
            SceneId = "scene-missing-2",
            Trigger = "enter",
            Conditions = listOf [ HasItemCondition(ItemId = "no-item", Quantity = 1.0) :> EventCondition ],
            Outcomes = listOf [ EventOutcome(Type = "startQuest", QuestId = "no-quest") ],
            Active = true,
            Repeatable = false
        )
    let project =
        Records.withValues project [ "StartSceneId", box "scene-missing"; "Shops", box (listOf [ shop ]); "Events", box (listOf [ event ]) ]
    let problems = ContentLints.validateProjectContent project
    let categories = problems |> List.map (fun p -> p.Category) |> set
    for category in [ "scenes"; "transitions"; "dialogue"; "quests"; "shops"; "events" ] do
        Assert.Contains(category, categories)
    Assert.True(problems.Length >= 7)

[<Fact>]
let ``flags unreachable scenes as warnings`` () =
    let project = makeProject ()
    project.Scenes.Add(Tiles.CreateEmptyScene("scene-island", "Island", 4.0, 4.0))
    let problems = ContentLints.validateProjectContent project
    Assert.Contains(problems, fun p -> p.Severity = Severity.Warning && p.Message.Contains "Island")

// ── M5SystemsTests: packs in the Problems panel ──────────────────────────────

[<Fact>]
let ``surfaces pack problems in the problems panel validation`` () =
    let raw =
        ContentPack(
            Manifest = PackManifest(Id = "needy", Name = "needy", Version = "1.0.0", Dependencies = listOf [ PackDependency(PackId = "nope") ]),
            Content = PackContent(),
            Plugins = listOf []
        )
    let validated = PacksSchema.ValidateContentPack(JsonDefaults.ToElement raw)
    Assert.True(validated.Ok, String.Join("; ", validated.Errors))
    let project = Records.withValue (makeProject ()) "ContentPacks" (box (listOf [ PackInstallation(Pack = validated.Pack, Enabled = true) ]))
    let problems = ContentLints.validateProjectContent project
    Assert.Contains(problems, fun p -> p.Category = "packs" && p.Message.Contains "'nope'" && p.Subject = Some "needy")

// ── PolishAndEcosystemTests: sample templates ────────────────────────────────

let templateFactories () : seq<obj[]> = [ [| box "cozy" |]; [| box "quest" |] ]

[<Theory>]
[<MemberData(nameof templateFactories)>]
let ``template validates and has zero content problems`` (name: string) =
    let project = if name = "cozy" then Templates.CreateCozyFarmProject() else Templates.CreateQuestRpgProject()
    let result = ProjectMigrations.migrateProject (JsonSerializer.SerializeToNode(project, JsonDefaults.Options))
    Assert.True(result.Ok, String.Join("; ", result.Errors))
    Assert.Empty(ContentLints.validateProjectContent project)

// ── F#-only ──────────────────────────────────────────────────────────────────

[<Fact>]
let ``schema issues carry the path and message the string form joins`` () =
    let project = parse (v8Json ())
    let broken = Records.withValues project [ "Mode", box "bogus"; "CurrentYear", box 1.5 ]
    let issues = SchemaChecks.projectIssues broken
    Assert.Equal<string list>([ "mode"; "currentYear" ], issues |> List.map (fun i -> i.Path))
    Assert.Equal<string list>(SchemaChecks.validateProject broken, issues |> List.map string)
    Assert.Equal("currentYear: Expected integer, received 1.5", string issues.[1])

[<Fact>]
let ``exported games skip the editor-only fields and an absent player`` () =
    let json = BrokenProjects.starterJson ()
    json.["player"].["direction"] <- JsonValue.Create "north"
    let withPlayer = json.Deserialize<ExportedGame>(JsonDefaults.Options)
    Assert.Equal<string list>([ "player.direction: Invalid enum value. Expected 'up' | 'down' | 'left' | 'right', received 'north'" ], SchemaChecks.validateExportedGame withPlayer)
    json.Remove "player" |> ignore
    Assert.Empty(SchemaChecks.validateExportedGame (json.Deserialize<ExportedGame>(JsonDefaults.Options)))
