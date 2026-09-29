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

/// The first difference between two stable JSON strings, with context, for failure messages.
let private assertSameStable (label: string) (expected: string) (actual: string) =
    if expected <> actual then
        let mutable i = 0
        while i < expected.Length && i < actual.Length && expected.[i] = actual.[i] do
            i <- i + 1
        let around (s: string) = s.Substring(max 0 (i - 120), min 240 (s.Length - max 0 (i - 120)))
        failwithf "%s: stable JSON differs at %d\nexpected: …%s…\nactual:   …%s…" label i (around expected) (around actual)

let private assertResult (label: string) (expected: JsonElement) (result: MigrationResult<'T>) =
    Assert.Equal<string list>(strings (expected.GetProperty "errors"), List.ofSeq result.Errors)
    Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok)
    Assert.Equal(expected.GetProperty("fromVersion").GetDouble(), result.FromVersion)
    Assert.Equal(expected.GetProperty("migrated").GetBoolean(), result.Migrated)
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
    assertSameStable name (fixture.GetProperty("stable").GetString() |> string) (stableProject result.Data)
    Assert.Equal(fixture.GetProperty("hash").GetString(), TestProjects.hashJson (SchemaJson.encodeGameProject result.Data.Value))

[<Theory>]
[<MemberData(nameof projectFixtures)>]
let ``exported game migration matches TypeScript`` (name: string) =
    let fixture = golden name
    let exported = fixture.GetProperty "exported"
    let result = ProjectMigrations.migrateExportedGame (node (fixture.GetProperty "input"))
    assertResult name (exported.GetProperty "result") result
    let expected = exported.GetProperty "stable"
    let expectedText = if expected.ValueKind = JsonValueKind.Null then "null" else expected.GetString() |> string
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
    let expectedText = File.ReadAllText(Path.Combine(migratedDir, expected)).TrimEnd('\n')
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
