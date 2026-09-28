module FarmEngine.Authoring.Tests.ProjectCatalogTests

open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Json
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private same (expected: 'T) (actual: 'U) = Assert.Equal(StableJson.Stringify<'T> expected, StableJson.Stringify<'U> actual)

// ── The sample games against the TypeScript goldens ─────────────────────────

/// The golden content fixtures hold `migrateProject(clone(pinTimes(createXxx())))` recorded from
/// the TypeScript engine (tools/golden golden.gen.test.ts `writeContentFixtures`).
let samples : obj[] seq =
    [ "starter-farm"; "cozy-garden"; "quest-rpg"; "blank" ] |> Seq.map (fun name -> [| box name |])

let private build (name: string) =
    match name with
    | "starter-farm" -> ProjectCatalog.CreateInitialProject fixedTime
    | "cozy-garden" -> ProjectCatalog.CreateCozyFarmProject fixedTime
    | "quest-rpg" -> ProjectCatalog.CreateQuestRpgProject fixedTime
    | "blank" -> ProjectCatalog.CreateBlankProject fixedTime
    | _ -> invalidArg (nameof name) name

[<Theory; MemberData(nameof samples)>]
let ``sample projects match TypeScript`` (name: string) =
    let expected = StableJson.Stringify((readElement [ "Golden"; "content"; name + ".json" ]).GetProperty "project": JsonElement)
    let project = build name
    // The factory output is already in the current schema shape…
    assertSameStable name expected (StableJson.Stringify project)
    // …and the migration pipeline leaves it as it is.
    let migrated = ProjectMigrations.migrateProject (JsonSerializer.SerializeToNode(project, JsonDefaults.Options))
    Assert.True(migrated.Ok, String.concat "; " migrated.Errors)
    assertSameStable (name + " (migrated)") expected (StableJson.Stringify migrated.Data)

let templates : obj[] seq = seq {
    for id in ProjectCatalog.All do
        for now in [0.0; 123456789.0; 1790553600000.0] do yield [|box id; box now|]
}

[<Theory; MemberData(nameof templates)>]
let ``every template is a valid project that compiles to a cartridge`` (id: string) (now: float) =
    let project = ProjectCatalog.CreateProjectForTemplate(id, now)
    Assert.Equal(now, project.GameStartTime)
    Assert.Equal(now, project.CurrentTime)
    Assert.Empty(Problems.collect project |> Problems.errors)
    Assert.Empty(SchemaChecks.validateProject project)
    Assert.NotEmpty(CartridgeCompiler.Compile project)

// ── Template catalog and routing ─────────────────────────────────────────────

[<Fact>]
let ``template catalog mirrors the web app`` () =
    Assert.Equal<string list>([ "starter"; "cozy"; "quest"; "blank" ], ProjectCatalog.TemplateInfo |> Seq.map (fun t -> t.Id) |> List.ofSeq)
    Assert.Equal<string list>([ "Starter Farm"; "Cozy Garden"; "Quest RPG"; "Blank" ], ProjectCatalog.TemplateInfo |> Seq.map (fun t -> t.Name) |> List.ofSeq)
    Assert.Equal<string list>([ "starter"; "blank"; "cozy"; "quest" ], List.ofSeq ProjectCatalog.All)
    Assert.Equal<string list>([ "starter"; "cozy"; "quest" ], List.ofSeq ProjectCatalog.SampleIds)
    Assert.Null(ProjectCatalog.CreateProjectFromTemplate("starter", fixedTime))
    Assert.Null(ProjectCatalog.CreateProjectFromTemplate("blank", fixedTime))
    Assert.Equal("Cozy Garden", ProjectCatalog.CreateProjectFromTemplate("cozy", fixedTime).Name)
    Assert.Equal("Quest RPG", ProjectCatalog.CreateProjectForTemplate("quest", fixedTime).Name)
    Assert.Equal("Untitled Game", ProjectCatalog.CreateProjectForTemplate("blank", fixedTime).Name)
    Assert.Equal("My Farming Game", ProjectCatalog.CreateProjectForTemplate("starter", fixedTime).Name)
    Assert.Equal("My Farming Game", ProjectCatalog.CreateSampleProject("bogus", fixedTime).Name)
    Assert.Equal("Cozy Garden", ProjectCatalog.CreateSampleProject("cozy", fixedTime).Name)

[<Theory>]
[<InlineData(null); InlineData(""); InlineData("starter"); InlineData("blank"); InlineData("cozy"); InlineData("quest"); InlineData("unknown")>]
let ``sample routing and unknown template fallbacks`` (id: string) =
    let now = 42.0
    let starter = ProjectCatalog.CreateInitialProject now
    let blank = ProjectCatalog.CreateBlankProject now
    let factory =
        match id with
        | "cozy" -> Some(ProjectCatalog.CreateCozyFarmProject now)
        | "quest" -> Some(ProjectCatalog.CreateQuestRpgProject now)
        | _ -> None
    // Samples fall back to the starter farm, templates to the blank project (starter by name).
    same (defaultArg factory starter) (ProjectCatalog.CreateSampleProject(id, now))
    match factory with
    | Some project -> same project (ProjectCatalog.CreateProjectFromTemplate(id, now))
    | None -> Assert.Null(ProjectCatalog.CreateProjectFromTemplate(id, now))
    same (defaultArg factory (if id = "starter" then starter else blank)) (ProjectCatalog.CreateProjectForTemplate(id, now))

[<Theory>]
[<InlineData(0.0, "proj-0"); InlineData(35.0, "proj-z"); InlineData(36.0, "proj-10"); InlineData(-123456789.0, "proj--21i3v9")>]
[<InlineData(1790553600000.0, "proj-mukhds00"); InlineData(1700000000000.0, "proj-loyw3v28")>]
let ``project ids are the creation time in base 36`` (now: float, expected: string) =
    // Date.now().toString(36)
    Assert.Equal(expected, ProjectCatalog.NewProjectId now)

[<Fact>]
let ``new projects take the template the name and an id`` () =
    let cozy = ProjectCatalog.CreateCozyFarmProject fixedTime
    let named (id: string) = Records.withValues cozy [ "Id", box id; "Name", box "My Garden" ]
    same (named "proj-loyw3v28") (ProjectCatalog.CreateNewProject("cozy", "My Garden", null, fixedTime))
    same (named "custom-id") (ProjectCatalog.CreateNewProject("cozy", "My Garden", "custom-id", fixedTime))
    same (named "") (ProjectCatalog.CreateNewProject("cozy", "My Garden", "", fixedTime))
    Assert.Equal(fixedTime, ProjectCatalog.CreateNewProject("cozy", "Mine", null, fixedTime).GameStartTime)

// ── The starter content pack and player ──────────────────────────────────────

/// Port of the C# ContentDefaultTests (content-default.test.ts): the default game IS a content pack.
[<Fact>]
let ``the starter pack validates as a content pack and round trips`` () =
    let pack = ProjectCatalog.CreateContentDefaultPack()
    let validated = PacksSchema.ValidateContentPack(JsonDefaults.ToElement pack)
    Assert.True validated.Ok
    Assert.Empty validated.Errors
    Assert.Equal(StableJson.Stringify pack, StableJson.Stringify validated.Pack)

[<Fact>]
let ``the starter pack expresses the complete built in catalog`` () =
    let pack = ProjectCatalog.CreateContentDefaultPack()
    Assert.Equal("content-default", pack.Manifest.Id)
    Assert.True pack.Manifest.Base
    // Built-in items first, then the crafting and extensibility showcases appended.
    let defaults = Builtin.items ()
    same defaults (pack.Content.Items |> Seq.take defaults.Count |> Seq.toList)
    Assert.Equal(defaults.Count + 9, pack.Content.Items.Count)
    Assert.Equal(9, pack.Content.Crops.Count)
    Assert.NotEmpty pack.Content.Recipes
    Assert.NotEmpty pack.Content.MachineTypes
    Assert.NotEmpty pack.Content.NodeTypes
    Assert.NotEmpty pack.Content.AnimalSpecies
    Assert.NotEmpty pack.Content.FishTables
    Assert.Equal<string list>([ "sun"; "rain"; "storm"; "snow" ], pack.Content.WeatherTypes |> Seq.map (fun w -> w.Id) |> List.ofSeq)
    Assert.Equal("scene-farm", pack.Content.Scenes.[0].Id)
    Assert.Equal<string list>([ "npc-farmer"; "npc-merchant" ], pack.Content.Npcs |> Seq.map (fun n -> n.Id) |> List.ofSeq)
    Assert.Equal<string list>([ "quest-first-harvest"; "quest-go-shopping" ], pack.Content.Quests |> Seq.map (fun q -> q.Id) |> List.ofSeq)
    Assert.Equal("shop-general", pack.Content.Shops.[0].Id)
    Assert.Equal(6, pack.Content.PlayerStart.Inventory.Count)

[<Fact>]
let ``the starter project is seeded entirely from the pack`` () =
    let project = starter ()
    Assert.Equal("scene-farm", project.Scenes.[0].Id)
    Assert.Equal("scene-farm", project.StartSceneId)
    Assert.Equal("scene-farm", project.Player.SceneId)
    Assert.Equal(58, project.Items.Count)
    Assert.Equal(2, project.Quests.Count)
    Assert.Equal(9, project.CustomCrops.Count)

[<Fact>]
let ``the default player is exactly the web one`` () =
    Assert.Equal(
        StableJson.Stringify(
            JsonDocument.Parse(
                """{"x":5,"y":5,"direction":"down","sceneId":"scene-farm","inventory":[],"maxInventorySize":20,"money":100,"activeQuests":[],"completedQuests":[],"pixelX":0,"pixelY":0,"targetX":0,"targetY":0}"""
            ).RootElement: JsonElement),
        StableJson.Stringify(ProjectCatalog.CreateDefaultPlayer "scene-farm"))

/// Port of the C# PolishAndEcosystemTests quest template case (m8 ecosystem).
[<Fact>]
let ``the quest template wires the elder chain end to end`` () =
    let content = ContentCompiler.compile (ProjectCatalog.CreateQuestRpgProject 0.0)
    let elder = content.Npcs |> Seq.find (fun n -> n.Id = "npc-elder")
    Assert.Equal("quest-rebuild-square", elder.Dialogue.[0].Options.[0].OfferQuestId)
    let feast = content.Quests |> Seq.find (fun q -> q.Id = "quest-festival-feast")
    Assert.Equal<string list>([ "quest-rebuild-square" ], List.ofSeq feast.Prerequisites)

// ── Independence of fresh projects ───────────────────────────────────────────

[<Fact>]
let ``fresh projects do not share mutable defaults maps or catalog entries`` () =
    let original = ProjectCatalog.CreateInitialProject 0.0
    let baseline = StableJson.Stringify original
    let other = ProjectCatalog.CreateInitialProject 0.0
    other.Scenes[0].Tiles.Clear()
    other.Items.Clear()
    other.Settings.Calendar.Seasons.Clear()
    other.Weather.Types.Clear()
    Assert.Equal(baseline, StableJson.Stringify original)
    Assert.Equal(baseline, StableJson.Stringify(ProjectCatalog.CreateInitialProject 0.0))

[<Fact>]
let ``sample transformations leave the initial game independent`` () =
    let starter = ProjectCatalog.CreateInitialProject 0.0
    let cozy = ProjectCatalog.CreateCozyFarmProject 0.0
    let quest = ProjectCatalog.CreateQuestRpgProject 0.0
    Assert.True starter.Settings.EnergyEnabled
    Assert.False cozy.Settings.EnergyEnabled
    Assert.Equal(10.0, starter.Player.Inventory[0].Quantity)
    Assert.Equal(20.0, cozy.Player.Inventory[0].Quantity)
    Assert.Equal(2, starter.Npcs.Count)
    Assert.Equal(3, quest.Npcs.Count)
