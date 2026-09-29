module FarmEngine.Authoring.Tests.ProjectCatalogTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private same (expected: GameProject) (actual: GameProject) =
    Assert.Equal(stableOf SchemaJson.encodeGameProject expected, stableOf SchemaJson.encodeGameProject actual)

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
    // The golden is the v8 project; the factories make v9 ones (docs/NUMERICS.md).
    let expected =
        Json.get "project" (readJson [ "Golden"; "content"; name + ".json" ])
        |> Migrations.migrateV8ToV9
        |> Json.set "schemaVersion" (JNumber Migrations.CurrentProjectSchemaVersion)
        |> Json.stableStringify
    let project = build name
    // The factory output is already in the current schema shape…
    assertSameStable name expected (stableOf SchemaJson.encodeGameProject project)
    // …and the migration pipeline leaves it as it is.
    let migrated = ProjectLoad.migrateProject (ProjectLoad.toJson project)
    Assert.True(migrated.Ok, String.concat "; " migrated.Errors)
    assertSameStable (name + " (migrated)") expected (stableOf SchemaJson.encodeGameProject migrated.Data.Value)

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
    Assert.Equal("Cozy Garden", ProjectCatalog.CreateProjectForTemplate("cozy", fixedTime).Name)
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
    let named (id: string) = { cozy with Id = id; Name = "My Garden" }
    same (named "proj-loyw3v28") (ProjectCatalog.CreateNewProject("cozy", "My Garden", null, fixedTime))
    same (named "custom-id") (ProjectCatalog.CreateNewProject("cozy", "My Garden", "custom-id", fixedTime))
    same (named "") (ProjectCatalog.CreateNewProject("cozy", "My Garden", "", fixedTime))
    Assert.Equal(fixedTime, ProjectCatalog.CreateNewProject("cozy", "Mine", null, fixedTime).GameStartTime)

// ── The starter content pack and player ──────────────────────────────────────

/// Port of the C# ContentDefaultTests (content-default.test.ts): the default game IS a content pack.
[<Fact>]
let ``the starter pack validates as a content pack and round trips`` () =
    let pack = ProjectCatalog.CreateContentDefaultPack()
    match PackRules.validateContentPack (SchemaJson.encodeContentPack pack) with
    | Ok validated -> Assert.Equal(stableOf SchemaJson.encodeContentPack pack, stableOf SchemaJson.encodeContentPack validated)
    | Error errors -> failwith (String.concat "; " errors)

[<Fact>]
let ``the starter pack expresses the complete built in catalog`` () =
    let pack = ProjectCatalog.CreateContentDefaultPack()
    Assert.Equal("content-default", pack.Manifest.Id)
    Assert.True pack.Manifest.Base
    // Built-in items first, then the crafting and extensibility showcases appended.
    let defaults = Builtin.items ()
    Assert.Equal<Item list>(defaults, pack.Content.Items |> List.take defaults.Length)
    Assert.Equal(defaults.Length + 9, pack.Content.Items.Length)
    Assert.Equal(9, pack.Content.Crops.Length)
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
    Assert.Equal(6, pack.Content.PlayerStart.Value.Inventory.Length)

[<Fact>]
let ``the starter project is seeded entirely from the pack`` () =
    let project = starter ()
    Assert.Equal("scene-farm", project.Scenes.[0].Id)
    Assert.Equal("scene-farm", project.StartSceneId)
    Assert.Equal("scene-farm", project.Player.SceneId)
    Assert.Equal(58, project.Items.Length)
    Assert.Equal(2, project.Quests.Length)
    Assert.Equal(9, project.CustomCrops.Value.Length)

[<Fact>]
let ``the default player is exactly the web one`` () =
    let expected =
        Json.parse """{"x":5,"y":5,"direction":"down","sceneId":"scene-farm","inventory":[],"maxInventorySize":20,"money":100,"activeQuests":[],"completedQuests":[],"pixelX":0,"pixelY":0,"targetX":0,"targetY":0}"""
    Assert.Equal(Result.map Json.stableStringify expected, Ok(stableOf SchemaJson.encodePlayer (ProjectCatalog.CreateDefaultPlayer "scene-farm")))

/// Port of the C# PolishAndEcosystemTests quest template case (m8 ecosystem).
[<Fact>]
let ``the quest template wires the elder chain end to end`` () =
    let content = ContentCompiler.compile (ProjectCatalog.CreateQuestRpgProject 0.0)
    let elder = content.Npcs |> Seq.find (fun n -> n.Id = "npc-elder")
    Assert.Equal(Some "quest-rebuild-square", elder.Dialogue.[0].Options.[0].OfferQuestId)
    let feast = content.Quests |> Seq.find (fun q -> q.Id = "quest-festival-feast")
    Assert.Equal<string list>([ "quest-rebuild-square" ], defaultArg feast.Prerequisites [])

// ── Independence of fresh projects ───────────────────────────────────────────

[<Fact>]
let ``fresh projects are equal and independent`` () =
    let original = ProjectCatalog.CreateInitialProject 0.0
    let baseline = stableOf SchemaJson.encodeGameProject original
    let other = ProjectCatalog.CreateInitialProject 0.0
    let emptied = { other with Scenes = []; Items = []; Weather = { other.Weather with Types = [] } }
    Assert.NotEqual(original, emptied)
    Assert.Equal(baseline, stableOf SchemaJson.encodeGameProject original)
    Assert.Equal(baseline, stableOf SchemaJson.encodeGameProject (ProjectCatalog.CreateInitialProject 0.0))

[<Fact>]
let ``sample transformations leave the initial game independent`` () =
    let starter = ProjectCatalog.CreateInitialProject 0.0
    let cozy = ProjectCatalog.CreateCozyFarmProject 0.0
    let quest = ProjectCatalog.CreateQuestRpgProject 0.0
    Assert.True starter.Settings.EnergyEnabled
    Assert.False cozy.Settings.EnergyEnabled
    Assert.Equal(10.0, starter.Player.Inventory[0].Quantity)
    Assert.Equal(20.0, cozy.Player.Inventory[0].Quantity)
    Assert.Equal(2, starter.Npcs.Length)
    Assert.Equal(3, quest.Npcs.Length)

// ── Project list: rename and duplicate ──────────────────────────────────────

[<Fact>]
let ``duplicate copies the project under a new id with its own game id`` () =
    let project = starter ()
    let project = { project with Export = Some(Defaults.newExportSettings project) }
    let newId = ProjectCatalog.NewProjectId 1790553600000.0
    let copy = ProjectList.Duplicate(project, newId, null)
    Assert.Equal(newId, copy.Id)
    Assert.Equal(sprintf "%s (copy)" project.Name, copy.Name)
    Assert.Equal("Farm (copy)", ProjectList.CopyName "  Farm ")
    Assert.Equal("Untitled Game (copy)", ProjectList.CopyName "")
    Assert.NotEqual<string>(project.Export.Value.GameId, copy.Export.Value.GameId)
    Assert.Equal("local." + newId, copy.Export.Value.GameId)
    Assert.Equal(project.Export.Value.ExecutableName, copy.Export.Value.ExecutableName)
    // Everything else is the same game.
    same { project with Id = newId; Name = copy.Name; Export = copy.Export } copy
    Assert.Equal("Second Farm", (ProjectList.Duplicate(project, newId, " Second Farm ")).Name)
    Assert.True((ProjectList.Duplicate(starter (), newId, null)).Export.IsNone)
    let problems = errors copy
    Assert.True(problems.IsEmpty, describe problems)

[<Fact>]
let ``rename trims and refuses a blank name`` () =
    let project = starter ()
    let renamed = ProjectList.Rename(project, "  Moonlit Acres ")
    Assert.Equal("Moonlit Acres", renamed.Name)
    Assert.Equal(project.Version, renamed.Version)
    Assert.Same(project, ProjectList.Rename(project, "   "))
    Assert.Same(renamed, ProjectList.Rename(renamed, "Moonlit Acres"))
