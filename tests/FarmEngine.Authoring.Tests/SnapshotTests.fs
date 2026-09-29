/// Behavior snapshots of the authoring core: for every golden, fixture, sample and broken
/// project, what migration, the Problems pipeline, the content compiler and the cartridge
/// compiler make of it, pinned as hashes and message lists in `fixtures/authoring/snapshots.json`.
/// They hold the authoring core to its behavior while its data model changes underneath. After
/// an intended change, rerun with FARM_RECORD_SNAPSHOTS=1 to rewrite the file, and review its diff.
module FarmEngine.Authoring.Tests.SnapshotTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas

let private baseDir = AppContext.BaseDirectory

let private snapshotFile = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixtures", "authoring", "snapshots.json")

let private sha (bytes: byte[]) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant().Substring(0, 16)
let private shaText (text: string) = sha (Encoding.UTF8.GetBytes text)

let private attempt (f: unit -> 'T) : Result<'T, string> =
    try
        Ok(f ())
    with ex ->
        Error(ex.GetType().Name + ": " + ex.Message)

let private readJson (path: string) : JsonNode = JsonNode.Parse(File.ReadAllText path)

let private files (dir: string) (pattern: string) =
    let full = Path.Combine(baseDir, dir)
    if Directory.Exists full then
        Directory.GetFiles(full, pattern) |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofArray
    else
        []

/// Project-shaped JSON text from every golden and fixture, the broken projects and the code-built
/// samples and templates, by name.
let private sources () : (string * string) list =
    let projectKey (path: string) =
        match readJson path with
        | :? JsonObject as o when o.ContainsKey "project" -> o.["project"]
        | node -> node
    let text (node: JsonNode | null) = match node with null -> "null" | node -> node.ToJsonString()
    [ for path in files "Golden/content" "*.json" -> "content/" + Path.GetFileName path, text (projectKey path)
      for path in files "Golden/replays" "*.json" do
          if Path.GetFileName path <> "index.json" then
              yield "replays/" + Path.GetFileName path, text (projectKey path)
      for path in files "Golden/migrations" "project-v*.json" -> "migrations/" + Path.GetFileName path, text (readJson path).["input"]
      for path in files "Golden/migrations" "errors.json" do
          for case in (readJson path).AsArray() do
              yield "migrations/errors/" + string case.["name"], text case.["input"]
      for path in files "Migrated" "*.json" -> "migrated/" + Path.GetFileName path, File.ReadAllText path
      for path in files "Fixtures" "*.json" do
          let name = Path.GetFileName path
          if name <> "content-lints.json" && name <> "content-builtin.json" then
              yield "fixtures/" + name, File.ReadAllText path
      for case in BrokenProjects.cases -> "broken/" + case.Name, (BrokenProjects.json case).ToJsonString()
      yield "code/starter", ProjectLoad.toText (TestProjects.starter ())
      yield "code/blank", ProjectLoad.toText (TestProjects.blank ())
      yield "code/cozy", ProjectLoad.toText (ProjectCatalog.CreateCozyFarmProject(0.0))
      yield "code/quest", ProjectLoad.toText (ProjectCatalog.CreateQuestRpgProject(0.0))
      for id, project in TestProjects.templates () -> "code/template-" + id, ProjectLoad.toText project ]

let private strings (items: string seq) = JsonArray([| for s in items -> JsonValue.Create s :> JsonNode |])

let private problemLine (p: Problem) = sprintf "%s|%s|%s|%s|%s" p.SeverityName p.Code p.Path p.Message p.TargetKind

/// Everything the authoring core makes of one project text.
let private snapshot (text: string) : JsonObject =
    let o = JsonObject()
    let project = ProjectLoad.migrateProjectText text
    o["ok"] <- JsonValue.Create project.Ok
    o["fromVersion"] <- JsonValue.Create project.FromVersion
    o["migrated"] <- JsonValue.Create project.Migrated
    o["errors"] <- strings project.Errors
    match project.Data with
    | None -> ()
    | Some data ->
        o["json"] <- JsonValue.Create(shaText (Json.stringify (ProjectLoad.toJson data)))
        o["stable"] <- JsonValue.Create(shaText (Json.stableStringify (ProjectLoad.toJson data)))
        match attempt (fun () -> Problems.collect data) with
        | Ok problems -> o["problems"] <- strings (problems |> List.map problemLine)
        | Error message -> o["problems"] <- JsonValue.Create("throws " + message)
        match attempt (fun () -> ContentCompiler.compile data) with
        | Ok content -> o["content"] <- JsonValue.Create(shaText (Json.stableStringify (SchemaJson.encodeGameContent content)))
        | Error message -> o["content"] <- JsonValue.Create("throws " + message)
        match attempt (fun () -> CartridgeCompiler.Compile data) with
        | Ok bytes -> o["cartridge"] <- JsonValue.Create(sha bytes)
        | Error message -> o["cartridge"] <- JsonValue.Create("throws " + message)
    let game = ProjectLoad.migrateExportedGameText text
    o["gameOk"] <- JsonValue.Create game.Ok
    o["gameErrors"] <- strings game.Errors
    match game.Data with
    | None -> ()
    | Some data -> o["game"] <- JsonValue.Create(shaText (Json.stableStringify (SchemaJson.encodeExportedGame data)))
    o

let private options = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

[<Fact>]
let ``the authoring core reproduces its recorded snapshots`` () =
    let actual = JsonObject()
    for name, text in sources () do
        actual[name] <- snapshot text
    if Environment.GetEnvironmentVariable "FARM_RECORD_SNAPSHOTS" = "1" then
        Directory.CreateDirectory(Path.GetDirectoryName snapshotFile) |> ignore
        File.WriteAllText(snapshotFile, actual.ToJsonString options + "\n")
    else
        let recorded = (readJson (Path.Combine(baseDir, "Snapshots", "snapshots.json"))).AsObject()
        let failures =
            [ for KeyValue(name, expected) in recorded do
                  match actual[name] with
                  | null -> yield name + ": missing"
                  | value when not (JsonNode.DeepEquals(value, expected)) ->
                      yield sprintf "%s:\nrecorded %s\nactual   %s" name (expected.ToJsonString options) (value.ToJsonString options)
                  | _ -> ()
              for KeyValue(name, _) in actual do
                  if not (recorded.ContainsKey name) then yield name + ": not recorded" ]
        Assert.True(List.isEmpty failures, String.concat "\n\n" (List.truncate 10 failures))
