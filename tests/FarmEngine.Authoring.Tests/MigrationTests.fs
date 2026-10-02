/// The F# project migrations against the TypeScript goldens (`fixtures/golden/migrations`).
module FarmEngine.Authoring.Tests.MigrationTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Schemas

let private goldenDir = Path.Combine(AppContext.BaseDirectory, "Golden", "migrations")
let private migratedDir = Path.Combine(AppContext.BaseDirectory, "Migrated")

let private load (path: string) : JsonElement = JsonDocument.Parse(File.ReadAllText path).RootElement

let private golden (name: string) = load (Path.Combine(goldenDir, name + ".json"))

let private node (element: JsonElement) : JsonNode = JsonNode.Parse(element.GetRawText())

/// Stable JSON of a result's data, "null" when there is none.
let private stable (encode: 'T -> Json) (data: 'T option) : string =
    match data with
    | None -> "null"
    | Some value -> Json.stableStringify (encode value)

let private stableProject = stable SchemaJson.encodeGameProject
let private stableGame = stable SchemaJson.encodeExportedGame

let private strings (element: JsonElement) : string list = [ for e in element.EnumerateArray() -> e.GetString() |> string ]

// The TypeScript goldens stop at schema v8; the F# pipeline adds the v8 → v9 step
// (docs/NUMERICS.md). Expected v8 results pass through that step before comparing, so the
// goldens keep pinning v1 → v8 and the step itself is checked on every golden.

/// A v8 golden result as v9: the numerics grid and the new version.
let private toV9 (json: Json) : Json =
    match json with
    | JObject _ -> json |> Migrations.migrateV8ToV9 |> Json.set "schemaVersion" (JNumber Migrations.CurrentProjectSchemaVersion)
    | other -> other

/// A golden's stable text as v9 ("null" stays).
let private toV9Text (text: string) : string =
    if text = "null" then text
    else
        match Json.parse text with
        | Ok json -> Json.stableStringify (toV9 json)
        | Error message -> failwith message

/// "… supports (8)." as the current engine says it.
let private currentVersionText (message: string) =
    message.Replace("supports (8)", "supports (" + JsNumber.format Migrations.CurrentProjectSchemaVersion + ")")

/// The first difference between two stable JSON strings, with context, for failure messages.
let private assertSameStable (label: string) (expected: string) (actual: string) =
    if expected <> actual then
        let mutable i = 0
        while i < expected.Length && i < actual.Length && expected.[i] = actual.[i] do
            i <- i + 1
        let around (s: string) = s.Substring(max 0 (i - 120), min 240 (s.Length - max 0 (i - 120)))
        failwithf "%s: stable JSON differs at %d\nexpected: …%s…\nactual:   …%s…" label i (around expected) (around actual)

let private assertResult (label: string) (expected: JsonElement) (result: MigrationResult<'T>) =
    Assert.Equal<string list>(strings (expected.GetProperty "errors") |> List.map currentVersionText, List.ofSeq result.Errors)
    Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok)
    Assert.Equal(expected.GetProperty("fromVersion").GetDouble(), result.FromVersion)
    // A v8 project is now migrated too (to v9).
    let fromV8 = result.Ok && result.FromVersion = 8.0
    Assert.Equal(expected.GetProperty("migrated").GetBoolean() || fromV8, result.Migrated)
    if expected.GetProperty("data").ValueKind = JsonValueKind.Null then
        Assert.True(result.Data.IsNone, label + ": expected no data")

let projectFixtures () : seq<obj[]> =
    Directory.GetFiles(goldenDir, "project-v*.json")
    |> Seq.map Path.GetFileNameWithoutExtension
    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> Seq.map (fun name -> [| box name |])

// ── Goldens ────────────────────────────────────────────────────────────────

[<Theory>]
[<MemberData(nameof projectFixtures)>]
let ``project migration matches TypeScript`` (name: string) =
    let fixture = golden name
    let result = ProjectMigrations.migrateProject (node (fixture.GetProperty "input"))
    assertResult name (fixture.GetProperty "result") result
    let expected = toV9Text (fixture.GetProperty("stable").GetString() |> string)
    assertSameStable name expected (stableProject result.Data)
    // The golden hash pins the v8 text; the v9 result hashes as the v9 text of the golden.
    let v8Hash = fixture.GetProperty("hash").GetString()
    let golden = match Json.parse (fixture.GetProperty("stable").GetString() |> string) with Ok j -> j | Error m -> failwith m
    Assert.Equal(v8Hash, TestProjects.hashJson golden)
    Assert.Equal(TestProjects.hashJson (toV9 golden), TestProjects.hashJson (SchemaJson.encodeGameProject result.Data.Value))

[<Theory>]
[<MemberData(nameof projectFixtures)>]
let ``exported game migration matches TypeScript`` (name: string) =
    let fixture = golden name
    let exported = fixture.GetProperty "exported"
    let result = ProjectMigrations.migrateExportedGame (node (fixture.GetProperty "input"))
    assertResult name (exported.GetProperty "result") result
    let expected = exported.GetProperty "stable"
    let expectedText = if expected.ValueKind = JsonValueKind.Null then "null" else toV9Text (expected.GetString() |> string)
    assertSameStable name expectedText (stableGame result.Data)

[<Fact>]
let ``failure cases match TypeScript`` () =
    for case in (golden "errors").EnumerateArray() do
        let name = case.GetProperty("name").GetString() |> string
        let input = node (case.GetProperty "input")
        assertResult name (case.GetProperty "project") (ProjectMigrations.migrateProject input)
        assertResult name (case.GetProperty "exported") (ProjectMigrations.migrateExportedGame input)

[<Fact>]
let ``the stable JSON writer reproduces the golden stable text`` () =
    for name in projectFixtures () |> Seq.map (fun row -> string row.[0]) do
        let fixture = golden name
        let data = JsonInterop.ofElement (fixture.GetProperty("result").GetProperty "data")
        Assert.Equal(fixture.GetProperty("stable").GetString(), Json.stableStringify data)

/// Hand-built edge cases recorded from TS (fixtures/projects/migrated).
[<Theory>]
[<InlineData("project-edge-v1.input.json", "project", "project-edge-v1.stable.json")>]
[<InlineData("project-edge-v1.input.json", "exported", "exported-edge-v1.stable.json")>]
[<InlineData("exported-legacy.input.json", "exported", "exported-legacy.stable.json")>]
let ``edge-case migrations match TypeScript`` (input: string, kind: string, expected: string) =
    let raw = JsonNode.Parse(File.ReadAllText(Path.Combine(migratedDir, input)))
    let expectedText = toV9Text (File.ReadAllText(Path.Combine(migratedDir, expected)).TrimEnd('\n'))
    let errors, data =
        if kind = "project" then
            let r = ProjectMigrations.migrateProject raw
            r.Errors, stableProject r.Data
        else
            let r = ProjectMigrations.migrateExportedGame raw
            r.Errors, stableGame r.Data
    Assert.Empty errors
    assertSameStable input expectedText data

// ── Malformed and edge inputs ──────────────────────────────────────────────

/// Every golden and edge input, plus malformed data that exercises the error paths.
let private differentialInputs () : (string * string) list =
    let fixtures =
        [ for name in projectFixtures () |> Seq.map (fun row -> string row.[0]) -> name, (golden name).GetProperty("input").GetRawText() ]
    let errorCases =
        [ for case in (golden "errors").EnumerateArray() ->
              "errors/" + (case.GetProperty("name").GetString() |> string), case.GetProperty("input").GetRawText() ]
    let edge =
        [ for file in [ "project-edge-v1.input.json"; "exported-legacy.input.json" ] -> file, File.ReadAllText(Path.Combine(migratedDir, file)) ]
    let v1 = (golden "project-v1").GetProperty("input").GetRawText()
    let v3 = (golden "project-v3").GetProperty("input").GetRawText()
    let patched (source: string) (patch: JsonObject -> unit) =
        match JsonNode.Parse source with
        | :? JsonObject as o ->
            patch o
            o.ToJsonString()
        | _ -> failwith "fixture input is not an object"
    let arrayOf (items: JsonNode list) =
        let array = JsonArray()
        for item in items do
            array.Add item
        array
    let malformed =
        [ "money is a string", patched v3 (fun o -> o.["player"].["money"] <- JsonValue.Create "lots")
          "scenes is a string", patched v1 (fun o -> o["scenes"] <- JsonValue.Create "not-a-list")
          "tile row is a number", patched v1 (fun o -> o.["scenes"].[0].["tiles"] <- arrayOf [ JsonValue.Create 3 ])
          "null tile", patched v1 (fun o -> o.["scenes"].[0].["tiles"] <- arrayOf [ arrayOf [ null ] ])
          "customCrops is an object", patched v3 (fun o -> o["customCrops"] <- JsonObject())
          "null event", patched v3 (fun o -> o["events"] <- arrayOf [ null ])
          "fractional version", patched v3 (fun o -> o["schemaVersion"] <- JsonValue.Create 2.5)
          "version 0", patched v3 (fun o -> o["schemaVersion"] <- JsonValue.Create 0)
          "exported name is a number", """{"name":42}"""
          "array payload", "[1,2,3]"
          "empty object", "{}" ]
    fixtures @ errorCases @ edge @ malformed

let malformedCases () : seq<obj[]> = differentialInputs () |> Seq.map (fun (name, _) -> [| box name |])

let private malformedInput (name: string) = differentialInputs () |> List.find (fun (n, _) -> n = name) |> snd

/// Whatever the input, loading reports instead of throwing, the result is deterministic, and a
/// refused load says why.
[<Theory>]
[<MemberData(nameof malformedCases)>]
let ``migrations never throw and always explain a refusal`` (name: string) =
    let text = malformedInput name
    let first = ProjectMigrations.migrateProjectText text
    let second = ProjectMigrations.migrateProject (JsonNode.Parse text)
    Assert.Equal(first.Ok, second.Ok)
    Assert.Equal<string list>(first.Errors, second.Errors)
    Assert.Equal(stableProject first.Data, stableProject second.Data)
    Assert.True(first.Ok || not first.Errors.IsEmpty, name + ": refused without an error")
    let game = ProjectMigrations.migrateExportedGameText text
    Assert.True(game.Ok || not game.Errors.IsEmpty, name + ": exported game refused without an error")

[<Fact>]
let ``malformed values are reported where they are`` () =
    let errors name = (ProjectMigrations.migrateProjectText (malformedInput name)).Errors
    Assert.Equal<string list>([ "player.money: Expected number, received string" ], errors "money is a string")
    Assert.Equal<string list>([ "Migration failed: Expected an array at '$.scenes'" ], errors "scenes is a string")
    Assert.Equal<string list>([ "Migration failed: Expected an array at '$.customCrops'" ], errors "customCrops is an object")
    Assert.Equal<string list>([ "Migration failed: Cannot read properties of null" ], errors "null event")

[<Fact>]
let ``migration does not change the caller's node`` () =
    let raw = JsonNode.Parse((golden "project-v1").GetProperty("input").GetRawText())
    let before = raw.ToJsonString()
    ProjectMigrations.migrateProject raw |> ignore
    ProjectMigrations.migrateExportedGame raw |> ignore
    Assert.Equal(before, raw.ToJsonString())

[<Fact>]
let ``invalid JSON text is reported, not thrown`` () =
    let result = ProjectMigrations.migrateProjectText "garbage"
    Assert.False result.Ok
    Assert.StartsWith("Project data is not valid JSON: ", result.Errors.[0])
    let exported = ProjectMigrations.migrateExportedGameText "{"
    Assert.StartsWith("Game data is not valid JSON: ", exported.Errors.[0])

// ── v8 → v9 (docs/NUMERICS.md) ─────────────────────────────────────────────

[<Fact>]
let ``v9 moves the values a project plays with onto the engine grid`` () =
    let parse text = match Json.parse text with Ok json -> json | Error message -> failwith message
    let v8 =
        parse
            """{"player":{"x":5.00001,"y":2.5,"money":10.5,"energy":99.99999,"maxEnergy":100},"currentTimeMinutes":390.0000004,
                "npcs":[{"id":"a","x":1.0001,"y":-0.5}],"animals":[{"id":"b","x":3,"y":4.25,"mood":72.5}],"items":[{"value":2.5}]}"""
    let v9 = Migrations.migrateV8ToV9 v8
    Assert.Equal(
        """{"player":{"x":5,"y":2.5,"money":11,"energy":100,"maxEnergy":100},"currentTimeMinutes":390,"npcs":[{"id":"a","x":1.0001220703125,"y":-0.5}],"animals":[{"id":"b","x":3,"y":4.25,"mood":73}],"items":[{"value":2.5}]}""",
        Json.stringify v9)
    // Values already on the grid stay as they are, and the step is idempotent.
    Assert.Equal(Json.stringify v9, Json.stringify (Migrations.migrateV8ToV9 v9))
    Assert.Equal(-3.0, JsNumber.roundHalfAway -2.5)
    Assert.Equal(0.0, JsNumber.roundHalfAway 0.49999999999999994)

[<Fact>]
let ``whole-number fields with a fraction are reported`` () =
    let project = TestProjects.starter ()
    let item = { project.Items.Head with Value = 12.5 }
    let problems = Problems.collect { project with Items = item :: project.Items.Tail }
    let found = problems |> List.filter (fun p -> p.Code = "numbers.offGrid")
    Assert.Equal<string list>([ "items[0].value" ], found |> List.map (fun p -> p.Path))
    Assert.EndsWith("it plays as 13", found.Head.Message)
    Assert.Empty(Problems.collect project |> List.filter (fun p -> p.Code = "numbers.offGrid"))

/// Web projects (the Romance pattern) can carry an NPC dialogue that differs from its copy in the
/// project list; the NPC's copy is the one the game plays (#43).
[<Fact>]
let ``loading reconciles dialogue copies with the NPC's copy, which the game plays`` () =
    let project = ProjectCatalog.CreateInitialProject 0.0
    let npc = project.Npcs |> List.find (fun n -> n.Id = "npc-farmer")
    let greeting = npc.Dialogue |> List.find (fun d -> d.Id = "dialogue-farmer-greeting")
    let invitation = { DialogueOption.Default with Text = "Would you like to go on a date?"; EventFlag = Some "date-asked" }
    let played = { greeting with Options = greeting.Options @ [ invitation ] }
    let onlyOnNpc = { DialogueOption.Default with Text = "Only on the NPC" } |> fun o -> { greeting with Id = "dialogue-npc-only"; Options = [ o ] }
    let mismatched =
        { project with
            Npcs = project.Npcs |> List.map (fun n -> if n.Id = npc.Id then { n with Dialogue = (n.Dialogue |> List.map (fun d -> if d.Id = greeting.Id then played else d)) @ [ onlyOnNpc ] } else n) }
    Assert.Equal(greeting, mismatched.Dialogues |> List.find (fun d -> d.Id = greeting.Id))
    let loaded =
        match (ProjectLoad.migrateProjectText (ProjectLoad.toText mismatched)) with
        | { Ok = true; Data = Some loaded } -> loaded
        | result -> failwithf "%A" result.Errors
    Assert.Equal(played, loaded.Dialogues |> List.find (fun d -> d.Id = greeting.Id))
    Assert.Equal(onlyOnNpc, List.last loaded.Dialogues)
    Assert.Equal(mismatched.Dialogues.Length + 1, loaded.Dialogues.Length)
    Assert.Empty(Problems.collect loaded |> List.filter (fun p -> p.Code = "dialogue.copyMismatch" || p.Code = "dialogue.notInProject"))
    // Saving what the Dialogue editor shows keeps the NPC-only option.
    let shown = ProjectContent.Dialogues mismatched |> Seq.find (fun d -> d.Id = greeting.Id)
    let saved = Document.run mismatched (UpsertDialogue shown)
    let onNpc = (saved.Npcs |> List.find (fun n -> n.Id = npc.Id)).Dialogue |> List.find (fun d -> d.Id = greeting.Id)
    Assert.Contains(invitation, onNpc.Options)
    // A project whose copies agree loads as the same value.
    Assert.Same(project, DialogueCopies.reconcile project)
