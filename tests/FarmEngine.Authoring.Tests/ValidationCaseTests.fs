/// The validators on every golden, fixture, sample and deliberately broken project: each broken
/// case (`BrokenProjects`) reaches the check it was written for, the load path reports the same
/// findings as the checks it runs, and the `ContentLints` findings on the golden, fixture and
/// sample projects are pinned to the ones recorded when the C# validators were retired
/// (`fixtures/projects/content-lints.json`).
module FarmEngine.Authoring.Tests.ValidationCaseTests

open System
open System.IO
open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

/// The result, or the exception type and message.
let private attempt (f: unit -> 'T) : Result<'T, string> =
    try
        Ok(f ())
    with ex ->
        Error(ex.GetType().FullName + ": " + ex.Message)

// ── Inputs ───────────────────────────────────────────────────────────────────

let private baseDir = AppContext.BaseDirectory

let private readFile (path: string) : Json =
    match Json.parse (File.ReadAllText path) with
    | Ok json -> json
    | Error message -> failwithf "%s: %s" path message

let private files (dir: string) (pattern: string) =
    Directory.GetFiles(Path.Combine(baseDir, dir), pattern) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray

/// Project-shaped JSON from every golden and fixture, by name.
let private jsonSources () : (string * (unit -> Json)) list =
    let projectKey (path: string) () =
        let json = readFile path
        if Json.has "project" json then Json.get "project" json else json
    [ for path in files "Golden/content" "*.json" -> "content/" + Path.GetFileName path, projectKey path
      for path in files "Golden/replays" "*.json" do
          if Path.GetFileName path <> "index.json" then
              yield "replays/" + Path.GetFileName path, projectKey path
      for path in files "Golden/migrations" "project-v*.json" -> "migrations/" + Path.GetFileName path, (fun () -> Json.get "input" (readFile path))
      for path in files "Golden/migrations" "errors.json" do
          match readFile path with
          | JArray cases ->
              for case in cases do
                  let name = Json.toJsString (Json.get "name" case)
                  yield "migrations/errors/" + name, (fun () -> Json.get "input" case)
          | _ -> ()
      for path in files "Migrated" "project-*.json" -> "migrated/" + Path.GetFileName path, (fun () -> readFile path)
      for path in files "Fixtures" "project-v*.json" -> "fixtures/" + Path.GetFileName path, (fun () -> readFile path)
      yield "fixtures/packs-project.json", (fun () -> readFile (Path.Combine(baseDir, "Fixtures", "packs-project.json"))) ]

/// Sample, template and default projects built in code, by name.
let private codeSources () : (string * (unit -> GameProject)) list =
    [ yield "starter", starter
      yield "blank", blank
      yield "cozy factory", (fun () -> ProjectCatalog.CreateCozyFarmProject(0.0))
      yield "quest factory", (fun () -> ProjectCatalog.CreateQuestRpgProject(0.0))
      for id, _ in templates () -> "template " + id, (fun () -> ProjectCatalog.CreateProjectForTemplate(id, 0.0)) ]

let private decode (decoder: Path -> Json -> 'T) (json: Json) : 'T option =
    match Decode.run decoder json with
    | Ok value -> Some value
    | Error _ -> None

/// The migrated form (raw migration), whatever its validity.
let private migrated (raw: Json) : Json option =
    match (Migrations.migrateProjectRaw raw).Data with
    | Some(JObject _ as json) -> Some json
    | _ -> None

let private editorOnlyKeys =
    [ "id"; "mode"; "selectedTileType"; "selectedNPCId"; "selectedItemId"; "currentTime"; "eventFlags"; "selectedTileVisual"; "rngState" ]

/// A project's JSON as exported games: as is (editor-only keys ride as passthrough keys),
/// without the editor-only keys (the web export) and without the player too.
let private exportedForms (json: Json) : (string * ExportedGame) list =
    let without (keys: string list) =
        match json with
        | JObject members -> JObject(members |> List.filter (fun (key, _) -> not (List.contains key keys)))
        | other -> other
    [ "as exported game", json
      "exported", without editorOnlyKeys
      "exported without player", without ("player" :: editorOnlyKeys) ]
    |> List.choose (fun (form, json) -> decode SchemaJson.decodeExportedGame json |> Option.map (fun game -> form, game))

let jsonSourceNames () : seq<obj[]> = jsonSources () |> Seq.map (fun (name, _) -> [| box name |])
let codeSourceNames () : seq<obj[]> = codeSources () |> Seq.map (fun (name, _) -> [| box name |])
let brokenCaseNames () : seq<obj[]> = BrokenProjects.cases |> Seq.map (fun c -> [| box c.Name |])

let private brokenCase (name: string) = BrokenProjects.cases |> List.find (fun c -> c.Name = name)

// ── The load path reports what the checks report ─────────────────────────────

/// Loading reports the checks' findings (capped at 20) and refuses exactly when they find any;
/// the checks never throw.
let private assertLoadMatchesChecks (label: string) (raw: Json) =
    let result = ProjectLoad.migrateProject raw
    match migrated raw |> Option.bind (decode SchemaJson.decodeGameProject) with
    | Some project ->
        let errors = SchemaChecks.validateProject project
        Assert.Equal<string list>(List.truncate 20 errors, if result.Ok then [] else result.Errors |> List.truncate 20)
        Assert.Equal(errors.IsEmpty, result.Ok)
    | None -> Assert.False(result.Ok, label + ": an unreadable project loaded")
    let game = ProjectLoad.migrateExportedGame raw
    Assert.True(game.Ok || not game.Errors.IsEmpty, label + ": exported game refused without an error")

[<Theory>]
[<MemberData(nameof jsonSourceNames)>]
let ``golden and fixture projects load and check without throwing`` (name: string) =
    let raw = (jsonSources () |> List.find (fun (n, _) -> n = name) |> snd) ()
    let mutable checkedForms = 0
    match decode SchemaJson.decodeGameProject raw with
    | Some project ->
        Assert.True((attempt (fun () -> SchemaChecks.validateProject project, SchemaChecks.lintProject project)) |> Result.isOk)
        checkedForms <- checkedForms + 1
    | None -> ()
    match migrated raw with
    | Some json ->
        assertLoadMatchesChecks name raw
        for form, game in exportedForms json do
            match attempt (fun () -> SchemaChecks.validateExportedGame game) with
            | Ok _ -> checkedForms <- checkedForms + 1
            | Error message -> failwithf "%s (%s): %s" name form message
    | None -> ()
    // Every source but the malformed migration inputs yields a typed project.
    if not (name.StartsWith("migrations/errors/", StringComparison.Ordinal)) then
        Assert.True(checkedForms > 0, name + ": nothing checked")

/// The migrated exported-game goldens (`exported-*.stable.json`) are valid exported games.
[<Fact>]
let ``the migrated exported-game goldens are valid`` () =
    let paths = files "Migrated" "exported-*.stable.json"
    Assert.NotEmpty paths
    for path in paths do
        match decode SchemaJson.decodeExportedGame (readFile path) with
        | Some game -> Assert.Empty(SchemaChecks.validateExportedGame game)
        | None -> failwithf "%s does not parse as an exported game" path

[<Theory>]
[<MemberData(nameof codeSourceNames)>]
let ``sample and template projects are valid in every exported form`` (name: string) =
    let project = (codeSources () |> List.find (fun (n, _) -> n = name) |> snd) ()
    Assert.Empty(SchemaChecks.validateProject project)
    let json = ProjectLoad.toJson project
    assertLoadMatchesChecks name json
    for form, game in exportedForms json do
        let errors = SchemaChecks.validateExportedGame game
        Assert.True(errors.IsEmpty, sprintf "%s (%s): %s" name form (String.concat "\n" errors))

// ── Content lints: the recorded findings ─────────────────────────────────────

/// The findings the F# `ContentLints` matched with the C# `Validation`, finding for finding, when
/// the C# engine was retired. After an intended lint change, rerun this test with
/// FARM_RECORD_CONTENT_LINTS=1 to rewrite the file, and review its diff.
let private contentLintsFile = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixtures", "projects", "content-lints.json")

let private severityName (severity: Severity) =
    match severity with
    | Severity.Error -> "error"
    | Severity.Warning -> "warning"
    | Severity.Info -> "info"

let private lintLines (project: GameProject) : string list =
    match attempt (fun () -> ContentLints.validateProjectContent project) with
    | Ok lints -> [ for l in lints -> sprintf "%s|%s|%s|%s" (severityName l.Severity) l.Category l.Message (defaultArg l.Subject "") ]
    | Error message -> [ "throws|" + message ]

/// Findings per project: every golden and fixture as is and migrated, and every code-built sample.
let private contentLintFindings () : (string * string list) list =
    [ for name, load in jsonSources () do
          let raw = load ()
          match decode SchemaJson.decodeGameProject raw with
          | Some project -> yield name + " (as is)", lintLines project
          | None -> ()
          match migrated raw |> Option.bind (decode SchemaJson.decodeGameProject) with
          | Some project -> yield name + " (migrated)", lintLines project
          | None -> ()
      for name, build in codeSources () -> name, lintLines (build ()) ]

[<Fact>]
let ``content lints report the recorded findings on golden, fixture and sample projects`` () =
    let actual = contentLintFindings ()
    if Environment.GetEnvironmentVariable "FARM_RECORD_CONTENT_LINTS" = "1" then
        let json = JObject [ for name, lines in actual -> name, JArray(List.map JString lines) ]
        File.WriteAllText(contentLintsFile, Json.stringifyIndented json + "\n")
    else
        let recorded =
            match readFile (Path.Combine(baseDir, "Fixtures", "content-lints.json")) with
            | JObject members -> members
            | _ -> failwith "content-lints.json is not an object"
        Assert.Equal<string list>(List.map fst recorded, List.map fst actual)
        for name, lines in actual do
            let expected = field name recorded |> function JArray items -> List.choose Json.asString items | _ -> []
            Assert.True((expected = lines), sprintf "%s: recorded\n%s\nactual\n%s" name (String.concat "\n" expected) (String.concat "\n" lines))

// ── Broken projects ──────────────────────────────────────────────────────────

let private hasPrefix (prefix: string) (entries: string list) =
    entries |> List.exists (fun e -> e.StartsWith(prefix, StringComparison.Ordinal))

[<Theory>]
[<MemberData(nameof brokenCaseNames)>]
let ``every broken project reaches the check it was written for`` (name: string) =
    let case = brokenCase name
    let raw = BrokenProjects.json case |> Net.JsonInterop.ofNode
    match BrokenProjects.tryProject case with
    | Error issue ->
        // The typed parse refuses it; so does loading, with the same issue.
        Assert.True(case.Expect |> List.exists (function BrokenProjects.Decode _ -> true | _ -> false), sprintf "%s does not parse: %s" name issue)
        Assert.False (ProjectLoad.migrateProject raw).Ok
    | Ok project ->
        Assert.True(attempt (fun () -> SchemaChecks.lintProject project) |> Result.isOk)
        if case.Typed project = project then
            assertLoadMatchesChecks name raw
        for form, game in exportedForms raw do
            Assert.True(attempt (fun () -> SchemaChecks.validateExportedGame game) |> Result.isOk, sprintf "%s (%s) throws" name form)
    for expect in case.Expect do
        match expect with
        | BrokenProjects.Parse prefix ->
            let errors = SchemaChecks.validateProject (BrokenProjects.project case)
            Assert.True(hasPrefix prefix errors, sprintf "%s: expected %s in\n%s" name prefix (String.concat "\n" errors))
        | BrokenProjects.Lint prefix ->
            let lints = SchemaChecks.lintProject (BrokenProjects.project case)
            Assert.True(hasPrefix prefix lints, sprintf "%s: expected %s in\n%s" name prefix (String.concat "\n" lints))
        | BrokenProjects.Content text ->
            let messages = ContentLints.validateProjectContent (BrokenProjects.project case) |> List.map (fun l -> l.Message)
            Assert.True(messages |> List.exists (fun m -> m.Contains(text, StringComparison.Ordinal)),
                        sprintf "%s: expected %s in\n%s" name text (String.concat "\n" messages))
        | BrokenProjects.NoContent text ->
            let messages = ContentLints.validateProjectContent (BrokenProjects.project case) |> List.map (fun l -> l.Message)
            Assert.False(messages |> List.exists (fun m -> m.Contains(text, StringComparison.Ordinal)), sprintf "%s: unexpected %s" name text)
        | BrokenProjects.Decode issue ->
            Assert.Equal(Error issue, BrokenProjects.tryProject case)

[<Fact>]
let ``the combined broken project hits the 20-error cap`` () =
    let case = brokenCase "everything at once"
    let result = ProjectLoad.migrateProject (BrokenProjects.json case |> Net.JsonInterop.ofNode)
    Assert.False result.Ok
    Assert.True((result.Errors.Length = 20), String.concat "\n" result.Errors)
    Assert.True(SchemaChecks.validateProject (BrokenProjects.project case) |> List.length > 20)

/// A `rngState` an exported game carries as a passthrough key is checked.
[<Fact>]
let ``exported game passthrough rngState is checked`` () =
    let json = BrokenProjects.starterJson ()
    json.Remove "id" |> ignore
    let gameWith (rngState: string) =
        json.["rngState"] <- System.Text.Json.Nodes.JsonNode.Parse rngState
        decodeJson SchemaJson.decodeExportedGame (Net.JsonInterop.ofNode json)
    let expected =
        [ """{"algorithm":"xoshiro128ss","s":[1,2,3,4]}""", []
          """{"algorithm":"mt19937","s":[1,2,3]}""",
          [ "rngState.algorithm: Invalid literal value, expected \"xoshiro128ss\""; "rngState.s: Expected a tuple of 4 integers" ]
          """{"algorithm":"xoshiro128ss","s":[1,2,3,4,5]}""", [ "rngState.s: Expected a tuple of 4 integers" ] ]
    for rngState, errors in expected do
        Assert.Equal<string list>(errors, SchemaChecks.validateExportedGame (gameWith rngState) |> List.filter (fun e -> e.StartsWith "rngState"))
    for rngState in [ """{"algorithm":null,"s":null}"""; """{"s":[1,2,3,4]}"""; """{"algorithm":"xoshiro128ss"}"""; "null" ] do
        Assert.True(attempt (fun () -> SchemaChecks.validateExportedGame (gameWith rngState)) |> Result.isOk, rngState)
