/// Realistic projects and lookups shared by the authoring tests.
module FarmEngine.Authoring.Tests.TestProjects

open System.Collections.Generic
open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas

/// The starter farm with wall-clock fields pinned.
let starter () = ProjectCatalog.CreateInitialProject(0.0)

let blank () = ProjectCatalog.CreateBlankProject(0.0)

/// Every "New Project" template, by id.
let templates () : (string * GameProject) list =
    ProjectCatalog.All |> List.ofSeq |> List.map (fun id -> id, ProjectCatalog.CreateProjectForTemplate(id, 0.0))

let scene (project: GameProject) (sceneId: string) : Scene =
    project.Scenes |> Seq.find (fun s -> s.Id = sceneId)

let farm (project: GameProject) = scene project "scene-farm"

let tile (project: GameProject) (sceneId: string) (x: int) (y: int) : Tile = (scene project sceneId).Tiles.[y].[x]

let apply (edit: Edit) (project: GameProject) : GameProject = Document.run project edit

let errors (project: GameProject) = Problems.collect project |> Problems.errors

/// Problems formatted for assertion messages.
let describe (problems: Problem list) =
    problems |> List.map (fun p -> sprintf "%s %s: %s" p.Code p.Path p.Message) |> String.concat "\n"

let item (id: string) (name: string) : Item =
    Item(Id = id, Name = name, Description = name, Type = ItemTypes.Material, Stackable = true, MaxStack = 99.0, Value = 5.0)

let npc (project: GameProject) (id: string) : Npc = project.Npcs |> Seq.find (fun n -> n.Id = id)

/// A nullable string as text ("" when null), for `Assert.Equal`.
let orEmpty (value: string | null) : string =
    match value with
    | null -> ""
    | s -> s

/// A .NET list from a sequence (the schema records hold `List<T>`).
let listOf (items: seq<'T>) : List<'T> = List<'T>(items)

/// A copy of a .NET list with one more element.
let appended (extra: 'T) (items: List<'T>) : List<'T> =
    let next = List<'T>(items)
    next.Add extra
    next

/// Asserts a `Result` is the given error message.
let expectError (expected: string) (result: Result<'T, string>) =
    match result with
    | Error message -> Assert.Equal(expected, message)
    | Ok _ -> failwithf "expected the error %A" expected

/// hash.ts: FNV-1a 64-bit (as two 32-bit lanes) over a stable JSON text — the hash the golden
/// fixtures record.
let hashText (text: string) : string =
    let mutable h1 = 0x811c9dc5u
    let mutable h2 = 0xcbf29ce4u
    for ch in text do
        let c = uint32 ch
        h1 <- (h1 ^^^ c) * 0x01000193u
        h2 <- (h2 ^^^ ((c <<< 1) ||| 1u)) * 0x01000193u
    h1.ToString("x8") + h2.ToString("x8")

/// `hashState`: the golden hash of a value's stable JSON.
let hashState (value: 'T) : string = hashText (FarmEngine.Json.StableJson.Stringify<'T> value)

/// The fixed `now` of the golden content fixtures (tools/golden golden.gen.test.ts FIXED_TIME).
let fixedTime = 1_700_000_000_000.0

/// A file shipped next to the tests (`Fixtures/`, `Golden/`, `Migrated/`).
let testFile (parts: string list) : string =
    System.IO.Path.Combine(Array.ofList (System.AppContext.BaseDirectory :: parts))

/// Stable JSON of one of the shared JSON files, parsed.
let readElement (parts: string list) : System.Text.Json.JsonElement =
    System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(testFile parts)).RootElement

/// Asserts two stable JSON strings are equal, reporting the first difference with context.
let assertSameStable (label: string) (expected: string) (actual: string) =
    if expected <> actual then
        let mutable i = 0
        while i < expected.Length && i < actual.Length && expected.[i] = actual.[i] do
            i <- i + 1
        let around (s: string) = s.Substring(max 0 (i - 160), min 320 (s.Length - max 0 (i - 160)))
        failwithf "%s: stable JSON differs at %d\nexpected: …%s…\nactual:   …%s…" label i (around expected) (around actual)
