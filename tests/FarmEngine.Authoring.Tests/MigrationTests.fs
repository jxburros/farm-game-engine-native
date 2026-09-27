/// The F# project migrations against the TypeScript goldens (`fixtures/golden/migrations`) and
/// against the C# port they replace (`FarmEngine.Schemas.Migrations`).
module FarmEngine.Authoring.Tests.MigrationTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net

type private CsMigrations = FarmEngine.Schemas.Migrations

let private goldenDir = Path.Combine(AppContext.BaseDirectory, "Golden", "migrations")
let private migratedDir = Path.Combine(AppContext.BaseDirectory, "Migrated")

let private load (path: string) : JsonElement = JsonDocument.Parse(File.ReadAllText path).RootElement

let private golden (name: string) = load (Path.Combine(goldenDir, name + ".json"))

let private node (element: JsonElement) : JsonNode = JsonNode.Parse(element.GetRawText())

/// `StableJson.Stringify` of a result's data, "null" when there is none.
let private stable (data: 'T) : string =
    if isNull (box data) then "null" else FarmEngine.Json.StableJson.Stringify data

let private strings (element: JsonElement) : string list = [ for e in element.EnumerateArray() -> e.GetString() |> string ]

/// The first difference between two stable JSON strings, with context, for failure messages.
let private assertSameStable (label: string) (expected: string) (actual: string) =
    if expected <> actual then
        let mutable i = 0
        while i < expected.Length && i < actual.Length && expected.[i] = actual.[i] do
            i <- i + 1
        let around (s: string) = s.Substring(max 0 (i - 120), min 240 (s.Length - max 0 (i - 120)))
        failwithf "%s: stable JSON differs at %d\nexpected: …%s…\nactual:   …%s…" label i (around expected) (around actual)

let private assertResult (label: string) (expected: JsonElement) (result: FarmEngine.Schemas.MigrationResult<'T>) =
    Assert.Equal<string list>(strings (expected.GetProperty "errors"), List.ofSeq result.Errors)
    Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok)
    Assert.Equal(expected.GetProperty("fromVersion").GetDouble(), result.FromVersion)
    Assert.Equal(expected.GetProperty("migrated").GetBoolean(), result.Migrated)
    if expected.GetProperty("data").ValueKind = JsonValueKind.Null then
        Assert.True(isNull (box result.Data), label + ": expected no data")

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
    assertSameStable name (fixture.GetProperty("stable").GetString() |> string) (stable result.Data)
    Assert.Equal(fixture.GetProperty("hash").GetString(), FarmEngine.Core.Hash.HashState result.Data)

[<Theory>]
[<MemberData(nameof projectFixtures)>]
let ``exported game migration matches TypeScript`` (name: string) =
    let fixture = golden name
    let exported = fixture.GetProperty "exported"
    let result = ProjectMigrations.migrateExportedGame (node (fixture.GetProperty "input"))
    assertResult name (exported.GetProperty "result") result
    let expected = exported.GetProperty "stable"
    let expectedText = if expected.ValueKind = JsonValueKind.Null then "null" else expected.GetString() |> string
    assertSameStable name expectedText (stable result.Data)

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

/// Hand-built edge cases recorded from TS for the C# port (tests/FarmEngine.Core.Tests/Fixtures/migrated).
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
            List.ofSeq r.Errors, stable r.Data
        else
            let r = ProjectMigrations.migrateExportedGame raw
            List.ofSeq r.Errors, stable r.Data
    Assert.Empty errors
    assertSameStable input expectedText data

// ── Differential: F# against the C# port ───────────────────────────────────

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

let differentialCases () : seq<obj[]> = differentialInputs () |> Seq.map (fun (name, _) -> [| box name |])

let private differentialInput (name: string) = differentialInputs () |> List.find (fun (n, _) -> n = name) |> snd

let private assertSameResult (label: string) (expected: FarmEngine.Schemas.MigrationResult<'T>) (actual: FarmEngine.Schemas.MigrationResult<'T>) =
    Assert.Equal<string list>(List.ofSeq expected.Errors, List.ofSeq actual.Errors)
    Assert.Equal(expected.Ok, actual.Ok)
    Assert.Equal(expected.FromVersion, actual.FromVersion)
    Assert.Equal(expected.Migrated, actual.Migrated)
    assertSameStable label (stable expected.Data) (stable actual.Data)

[<Theory>]
[<MemberData(nameof differentialCases)>]
let ``F# and C# migrations agree`` (name: string) =
    let text = differentialInput name
    assertSameResult name (CsMigrations.MigrateProject(JsonNode.Parse text)) (ProjectMigrations.migrateProject (JsonNode.Parse text))
    assertSameResult name (CsMigrations.MigrateExportedGame(JsonNode.Parse text)) (ProjectMigrations.migrateExportedGame (JsonNode.Parse text))
    assertSameResult name (CsMigrations.MigrateProject text) (ProjectMigrations.migrateProjectText text)

/// Step by step, the raw JSON (member order included) of each F# migration equals the C# one,
/// and where a C# step throws, the F# step fails with the same message.
[<Theory>]
[<MemberData(nameof differentialCases)>]
let ``each F# migration step produces the same raw JSON as C#`` (name: string) =
    let text = differentialInput name
    let fromVersion = Migrations.detectProjectVersion (JsonInterop.ofNode (JsonNode.Parse text))
    Assert.Equal(CsMigrations.DetectProjectVersion(JsonNode.Parse text), fromVersion)
    match JsonNode.Parse text with
    | :? JsonObject as start ->
        let mutable fs = JsonInterop.ofNode start
        let mutable cs = start
        let mutable v = fromVersion
        let mutable failed = false
        while not failed && v < Migrations.CurrentProjectSchemaVersion && Migrations.registry.ContainsKey v do
            let fsStep =
                try
                    Ok(Migrations.registry.[v] fs)
                with JsTypeError message ->
                    Error message
            let csStep =
                try
                    Ok(CsMigrations.Registry.[v].Invoke cs)
                with :? InvalidOperationException as ex ->
                    Error ex.Message
            match csStep, fsStep with
            | Ok c, Ok f ->
                Assert.True((JsonInterop.ofNode c = f), sprintf "%s: step %g differs\nC#: %s\nF#: %s" name v (c.ToJsonString()) (Json.stringify f))
                cs <- c
                fs <- f
            | Error c, Error f ->
                Assert.Equal(c, f)
                failed <- true
            | c, f -> failwithf "%s: step %g: C# %A but F# %A" name v c f
            v <- v + 1.0
    | _ -> ()

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
    Assert.Equal<string list>(List.ofSeq (CsMigrations.MigrateProject "garbage").Errors, List.ofSeq result.Errors)
    let exported = ProjectMigrations.migrateExportedGameText "{"
    Assert.StartsWith("Game data is not valid JSON: ", exported.Errors.[0])
