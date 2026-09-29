/// Realistic projects and lookups shared by the authoring tests.
module FarmEngine.Authoring.Tests.TestProjects

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
    { Item.Default with Id = id; Name = name; Description = name; Type = ItemTypes.Material; Stackable = true; MaxStack = 99.0; Value = 5.0 }

let npc (project: GameProject) (id: string) : Npc = project.Npcs |> Seq.find (fun n -> n.Id = id)

/// An optional string as text ("" when absent), for `Assert.Equal`.
let orEmpty (value: string option) : string = defaultArg value ""

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

/// `hashState`: the golden hash of a JSON value's stable text.
let hashJson (value: Json) : string = hashText (Json.stableStringify value)

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

/// A decoded value (the decode must succeed).
let decodeJson (decode: Path -> Json -> 'T) (json: Json) : 'T =
    match Decode.run decode json with
    | Ok value -> value
    | Error issue -> failwith issue

/// One of the shared JSON files, parsed.
let readJson (parts: string list) : Json =
    match Json.parse (System.IO.File.ReadAllText(testFile parts)) with
    | Ok json -> json
    | Error message -> failwith message

/// A project from its JSON (the typed parse, no migration).
let projectOf (json: Json) : GameProject = decodeJson SchemaJson.decodeGameProject json

/// A project written to JSON and read back.
let roundTrip (project: GameProject) : GameProject = projectOf (ProjectLoad.toJson project)

/// A pack that passes `PackRules.validateContentPack`.
let validPack (json: Json) : ContentPack =
    match PackRules.validateContentPack json with
    | Ok pack -> pack
    | Error errors -> failwith (String.concat "; " errors)

/// Stable JSON of an encoded value.
let stableOf (encode: 'T -> Json) (value: 'T) : string = Json.stableStringify (encode value)

/// The value under a key of an ordered map (fails when the key is absent).
let field (key: string) (members: (string * 'T) list) : 'T =
    match List.tryFind (fst >> (=) key) members with
    | Some(_, value) -> value
    | None -> failwithf "no %s key" key

/// A scene with the tile at (x, y) replaced by `f` of it.
let mapTile (x: int) (y: int) (f: Tile -> Tile) (scene: Scene) : Scene =
    { scene with Tiles = scene.Tiles |> List.mapi (fun yy row -> if yy <> y then row else row |> List.mapi (fun xx t -> if xx = x then f t else t)) }
