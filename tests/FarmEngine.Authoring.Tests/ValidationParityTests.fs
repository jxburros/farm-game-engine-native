/// Differential tests: the F# `SchemaChecks` and `ContentLints` against the C# `SchemaValidation`
/// and `Validation` they port. Same list, same order (or the same exception) on every golden
/// project, the migrated goldens, the sample and template projects and a generated set of broken
/// projects that trigger every check (`BrokenProjects`).
module FarmEngine.Authoring.Tests.ValidationParityTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Content
open FarmEngine.Json
open FarmEngine.Schemas

type private CsSchema = FarmEngine.Schemas.SchemaValidation
type private CsContent = FarmEngine.Core.Validation
type private CsMigrations = FarmEngine.Schemas.Migrations

// ── Comparing ────────────────────────────────────────────────────────────────

/// The result, or the exception type and message.
let private attempt (f: unit -> 'T) : Result<'T, string> =
    try
        Ok(f ())
    with ex ->
        Error(ex.GetType().FullName + ": " + ex.Message)

let private assertSame (label: string) (expected: Result<string list, string>) (actual: Result<string list, string>) =
    if expected <> actual then
        failwithf "%s: C# and F# differ\nC#: %A\nF#: %A" label expected actual

let private severityName (severity: Severity) =
    match severity with
    | Severity.Error -> "error"
    | Severity.Warning -> "warning"
    | Severity.Info -> "info"

let private csContent (project: GameProject) =
    [ for p in CsContent.ValidateProjectContent project -> sprintf "%s|%s|%s|%A" p.Severity p.Category p.Message (Option.ofObj p.Subject) ]

let private fsContent (project: GameProject) =
    [ for l in ContentLints.validateProjectContent project -> sprintf "%s|%s|%s|%A" (severityName l.Severity) l.Category l.Message l.Subject ]

/// The three project validators agree; returns how many findings they produced.
let private compareProject (label: string) (project: GameProject) =
    let validate = attempt (fun () -> List.ofSeq (CsSchema.ValidateProject project))
    assertSame (label + " / ValidateProject") validate (attempt (fun () -> SchemaChecks.validateProject project))
    let lint = attempt (fun () -> List.ofSeq (CsSchema.LintProject project))
    assertSame (label + " / LintProject") lint (attempt (fun () -> SchemaChecks.lintProject project))
    let content = attempt (fun () -> csContent project)
    assertSame (label + " / ValidateProjectContent") content (attempt (fun () -> fsContent project))
    [ validate; lint; content ] |> List.sumBy (function Ok xs -> xs.Length | Error _ -> 1)

/// Both F# exported-game validators agree with the C# (`exact`: also the pure one, which reads
/// nulls and NaN as they are where the C# round trip normalizes or throws).
let private compareExportedWith (exact: bool) (label: string) (game: ExportedGame) =
    let expected = attempt (fun () -> List.ofSeq (CsSchema.ValidateExportedGame game))
    assertSame (label + " / ValidateExportedGame (migrations)") expected (attempt (fun () -> ProjectMigrations.validateExportedGame game))
    if exact then
        assertSame (label + " / ValidateExportedGame") expected (attempt (fun () -> SchemaChecks.validateExportedGame game))

let private compareExported = compareExportedWith true

// ── Inputs ───────────────────────────────────────────────────────────────────

let private baseDir = AppContext.BaseDirectory

let private readJson (path: string) : JsonNode = JsonNode.Parse(File.ReadAllText path)

let private files (dir: string) (pattern: string) =
    Directory.GetFiles(Path.Combine(baseDir, dir), pattern) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray

/// Project-shaped JSON from every golden and fixture, by name.
let private jsonSources () : (string * (unit -> JsonNode)) list =
    let projectKey (path: string) () =
        match readJson path with
        | :? JsonObject as o when o.ContainsKey "project" -> o.["project"]
        | node -> node
    [ for path in files "Golden/content" "*.json" -> "content/" + Path.GetFileName path, projectKey path
      for path in files "Golden/replays" "*.json" do
          if Path.GetFileName path <> "index.json" then
              yield "replays/" + Path.GetFileName path, projectKey path
      for path in files "Golden/migrations" "project-v*.json" -> "migrations/" + Path.GetFileName path, (fun () -> (readJson path).["input"])
      for path in files "Golden/migrations" "errors.json" do
          for case in (readJson path).AsArray() do
              let name = string case.["name"]
              yield "migrations/errors/" + name, (fun () -> match case.["input"] with null -> null | input -> input.DeepClone())
      for path in files "Migrated" "project-*.json" -> "migrated/" + Path.GetFileName path, (fun () -> readJson path)
      for path in files "Fixtures" "project-v*.json" -> "fixtures/" + Path.GetFileName path, (fun () -> readJson path)
      yield "fixtures/packs-project.json", (fun () -> readJson (Path.Combine(baseDir, "Fixtures", "packs-project.json"))) ]

/// Sample, template and default projects built in code, by name.
let private codeSources () : (string * (unit -> GameProject)) list =
    [ yield "starter", TestProjects.starter
      yield "blank", TestProjects.blank
      yield "cozy factory", (fun () -> Templates.CreateCozyFarmProject(0.0))
      yield "quest factory", (fun () -> Templates.CreateQuestRpgProject(0.0))
      for id, _ in TestProjects.templates () -> "template " + id, (fun () -> Templates.CreateProjectForTemplate(id, 0.0)) ]

let private deserialize<'T> (node: JsonNode) : 'T option =
    match attempt (fun () -> node.Deserialize<'T>(JsonDefaults.Options)) with
    | Ok value when not (isNull (box value)) -> Some value
    | _ -> None

/// The migrated form (F# raw migration), whatever its validity.
let private migrated (raw: JsonNode) : JsonObject option =
    let result = Migrations.migrateProjectRaw (JsonInterop.ofNode raw)
    match result.Data |> Option.map JsonInterop.toNode with
    | Some(:? JsonObject as o) -> Some o
    | _ -> None

let private editorOnlyKeys =
    [ "id"; "mode"; "selectedTileType"; "selectedNPCId"; "selectedItemId"; "currentTime"; "eventFlags"; "selectedTileVisual"; "rngState" ]

/// A project's JSON as exported games: as is (editor-only keys ride as passthrough keys),
/// without the editor-only keys (the web export) and without the player too.
let private exportedForms (json: JsonObject) : (string * ExportedGame) list =
    let without (keys: string list) =
        let copy = json.DeepClone().AsObject()
        for key in keys do
            copy.Remove key |> ignore
        copy :> JsonNode
    [ "as exported game", json.DeepClone()
      "exported", without editorOnlyKeys
      "exported without player", without ("player" :: editorOnlyKeys) ]
    |> List.choose (fun (form, node) -> deserialize<ExportedGame> node |> Option.map (fun game -> form, game))

let jsonSourceNames () : seq<obj[]> = jsonSources () |> Seq.map (fun (name, _) -> [| box name |])
let codeSourceNames () : seq<obj[]> = codeSources () |> Seq.map (fun (name, _) -> [| box name |])
let brokenCaseNames () : seq<obj[]> = BrokenProjects.cases |> Seq.map (fun c -> [| box c.Name |])

let private brokenCase (name: string) = BrokenProjects.cases |> List.find (fun c -> c.Name = name)

// ── Goldens, migrated goldens, fixtures ──────────────────────────────────────

[<Theory>]
[<MemberData(nameof jsonSourceNames)>]
let ``F# and C# validators agree on golden and fixture projects`` (name: string) =
    let raw = (jsonSources () |> List.find (fun (n, _) -> n = name) |> snd) ()
    let mutable compared = 0
    match deserialize<GameProject> raw with
    | Some project ->
        compareProject (name + " (as is)") project |> ignore
        compared <- compared + 1
    | None -> ()
    match migrated raw with
    | Some json ->
        match deserialize<GameProject> json with
        | Some project ->
            compareProject (name + " (migrated)") project |> ignore
            compared <- compared + 1
        | None -> ()
        for form, game in exportedForms json do
            compareExported (sprintf "%s (migrated, %s)" name form) game
            compared <- compared + 1
    | None -> ()
    match Migrations.migrateExportedGameRaw(JsonInterop.ofNode raw).Data |> Option.map JsonInterop.toNode with
    | Some(:? JsonObject as json) ->
        deserialize<ExportedGame> json |> Option.iter (compareExported (name + " (migrated as exported game)"))
    | _ -> ()
    // Every source but the malformed migration inputs yields a typed project.
    if not (name.StartsWith("migrations/errors/", StringComparison.Ordinal)) then
        Assert.True(compared > 0, name + ": nothing compared")

/// The migrated exported-game goldens (`exported-*.stable.json`), as exported games.
[<Fact>]
let ``F# and C# agree on the migrated exported-game goldens`` () =
    let paths = files "Migrated" "exported-*.stable.json"
    Assert.NotEmpty paths
    for path in paths do
        match deserialize<ExportedGame> (readJson path) with
        | Some game -> compareExported (Path.GetFileName path) game
        | None -> failwithf "%s does not parse as an exported game" path

[<Theory>]
[<MemberData(nameof codeSourceNames)>]
let ``F# and C# validators agree on sample and template projects`` (name: string) =
    let project = (codeSources () |> List.find (fun (n, _) -> n = name) |> snd) ()
    compareProject name project |> ignore
    let json = JsonSerializer.SerializeToNode(project, JsonDefaults.Options).AsObject()
    for form, game in exportedForms json do
        compareExported (sprintf "%s (%s)" name form) game

// ── Broken projects ──────────────────────────────────────────────────────────

let private hasPrefix (prefix: string) (entries: string list) =
    entries |> List.exists (fun e -> e.StartsWith(prefix, StringComparison.Ordinal))

[<Theory>]
[<MemberData(nameof brokenCaseNames)>]
let ``F# and C# validators agree on broken projects`` (name: string) =
    let case = brokenCase name
    let project = BrokenProjects.project case
    compareProject name project |> ignore
    for form, game in exportedForms (BrokenProjects.json case) do
        compareExportedWith case.Exported (sprintf "%s (%s)" name form) game
    // The case reaches the check it was written for.
    for expect in case.Expect do
        match expect with
        | BrokenProjects.Parse prefix ->
            let errors = SchemaChecks.validateProject project
            Assert.True(hasPrefix prefix errors, sprintf "%s: expected %s in\n%s" name prefix (String.concat "\n" errors))
        | BrokenProjects.Lint prefix ->
            let lints = SchemaChecks.lintProject project
            Assert.True(hasPrefix prefix lints, sprintf "%s: expected %s in\n%s" name prefix (String.concat "\n" lints))
        | BrokenProjects.Content text ->
            let messages = ContentLints.validateProjectContent project |> List.map (fun l -> l.Message)
            Assert.True(messages |> List.exists (fun m -> m.Contains(text, StringComparison.Ordinal)),
                        sprintf "%s: expected %s in\n%s" name text (String.concat "\n" messages))
        | BrokenProjects.NoContent text ->
            let messages = ContentLints.validateProjectContent project |> List.map (fun l -> l.Message)
            Assert.False(messages |> List.exists (fun m -> m.Contains(text, StringComparison.Ordinal)), sprintf "%s: unexpected %s" name text)
        | BrokenProjects.Throws ->
            Assert.ThrowsAny<exn>(fun () -> SchemaChecks.validateProject project |> ignore) |> ignore

/// The broken projects through the migrations: the F# path (typed parse + F# checks) reports
/// the same errors as the C# one, including the 20-error cap.
[<Theory>]
[<MemberData(nameof brokenCaseNames)>]
let ``F# and C# migrations report the same validation errors on broken projects`` (name: string) =
    let text = (BrokenProjects.json (brokenCase name)).ToJsonString()
    let expected = CsMigrations.MigrateProject(JsonNode.Parse text)
    let actual = ProjectMigrations.migrateProject (JsonNode.Parse text)
    Assert.Equal<string list>(List.ofSeq expected.Errors, List.ofSeq actual.Errors)
    Assert.Equal(expected.Ok, actual.Ok)
    let expectedGame = CsMigrations.MigrateExportedGame(JsonNode.Parse text)
    let actualGame = ProjectMigrations.migrateExportedGame (JsonNode.Parse text)
    Assert.Equal<string list>(List.ofSeq expectedGame.Errors, List.ofSeq actualGame.Errors)
    Assert.Equal(expectedGame.Ok, actualGame.Ok)

[<Fact>]
let ``the combined broken project hits the 20-error cap on both paths`` () =
    let text = (BrokenProjects.json (brokenCase "everything at once")).ToJsonString()
    let result = ProjectMigrations.migrateProject (JsonNode.Parse text)
    Assert.False result.Ok
    Assert.True((result.Errors.Count = 20), String.Join("\n", result.Errors))
    Assert.True(SchemaChecks.validateProject (BrokenProjects.project (brokenCase "everything at once")) |> List.length > 20)

/// A `rngState` an exported game carries as a passthrough key is checked, as the C# round trip does.
[<Fact>]
let ``exported game passthrough rngState is checked like the C#`` () =
    let json = BrokenProjects.starterJson ()
    json.Remove "id" |> ignore
    let variants =
        [ """{"algorithm":"mt19937","s":[1,2,3]}"""
          """{"algorithm":null,"s":null}"""
          """{"s":[1,2,3,4]}"""
          """{"algorithm":"xoshiro128ss"}"""
          """{"algorithm":"xoshiro128ss","s":[1,2,3,4,5]}"""
          "null" ]
    for variant in variants do
        json.["rngState"] <- JsonNode.Parse variant
        let game = json.Deserialize<ExportedGame>(JsonDefaults.Options)
        compareExported ("rngState " + variant) game
    json.["rngState"] <- JsonNode.Parse """{"algorithm":"mt19937","s":[1,2,3]}"""
    Assert.Equal<string list>(
        [ "rngState.algorithm: Invalid literal value, expected \"xoshiro128ss\""; "rngState.s: Expected a tuple of 4 integers" ],
        SchemaChecks.validateExportedGame (json.Deserialize<ExportedGame>(JsonDefaults.Options)))
