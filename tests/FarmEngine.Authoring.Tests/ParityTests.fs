/// F# ↔ Rust parity (#117, #118). The schema exists as F# records (Schema.fs, SchemaJson.fs) and
/// as Rust serde types (crates/farm-sim/src/schema), and both sides can turn a project into game
/// content (F# `ContentCompiler`/`CartridgeCompiler` for cartridges, Rust
/// `state::create_content_from_project`/`StartState::from_project`/`Presentation::from_project` for
/// the editor's previews and sessions). These tests keep them in step:
///
/// - Rust records content for the projects of `fixtures/golden/content`; F# compiles the same
///   projects and must get the same content.
/// - F# records `fixtures/parity/*.json`: a kitchen-sink project and content that set every field
///   of every record, and the F# pipeline's output for the templates, the fixture projects and
///   the example packs. `crates/farm-sim/tests/fsharp_parity.rs` reads them: Rust must decode and
///   re-encode every field the same, and its pipeline must give the same content, start and
///   presentation. After an intended change, rerun with FARM_RECORD_PARITY=1 and run the Rust test.
module FarmEngine.Authoring.Tests.ParityTests

open System
open System.IO
open Microsoft.FSharp.Reflection
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private repo (parts: string list) = Path.Combine(Array.ofList (__SOURCE_DIRECTORY__ :: ".." :: ".." :: parts))

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error message -> failwith message

let private decode (decoder: Path -> Json -> 'T) (json: Json) : 'T =
    match Decode.run decoder json with
    | Ok value -> value
    | Error message -> failwith message

// ── Kitchen sink ─────────────────────────────────────────────────────────────

/// A value of every schema record type with every field set: options present, lists with one
/// element (every case, for a list of union values), numbers away from their defaults. Built by
/// reflection over the F# records, so a field added to Schema.fs is in the kitchen sink at once.
/// `Extra` stays empty: undeclared keys are kept by some records on one side only, and neither
/// side gives them a meaning.
type private KitchenSink() =
    let mutable counter = 0

    /// Numbers the Rust grids keep exactly; fractions where the schema wants 0..1.
    let number (field: string) : float =
        let lower = field.ToLowerInvariant()
        if [ "chance"; "fraction"; "density"; "score"; "difficulty" ] |> List.exists lower.Contains then 0.5
        else 2.0

    member this.Value(t: Type, field: string, depth: int) : obj =
        if t = typeof<string> then
            counter <- counter + 1
            box (sprintf "%s-%d" (if field.Length > 0 then field.ToLowerInvariant() else "s") counter)
        elif t = typeof<float> then box (number field)
        elif t = typeof<int> then box 2
        elif t = typeof<uint32> then box 2u
        elif t = typeof<bool> then box true
        // A kept playtest's engine state: opaque to F#, a typed `KeptState` to Rust.
        elif t = typeof<Json> && field = "KeptState" then
            box (JObject [ "tick", JNumber 2.0; "shopPurchasesToday", JObject []; "npcs", JObject []; "mineCurrentFloor", JNumber 2.0 ])
        elif t = typeof<Json> then box (JString "json")
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
            let inner = t.GetGenericArguments().[0]
            let some = FSharpType.GetUnionCases(t) |> Array.find (fun c -> c.Name = "Some")
            let none = FSharpType.GetUnionCases(t) |> Array.find (fun c -> c.Name = "None")
            if depth > 12 then FSharpValue.MakeUnion(none, [||])
            else FSharpValue.MakeUnion(some, [| this.Value(inner, field, depth + 1) |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
            let element = t.GetGenericArguments().[0]
            let items: obj list =
                if depth > 12 || field = "Extra" then []
                elif FSharpType.IsTuple element then
                    // A record (`z.record`): one key.
                    let types = FSharpType.GetTupleElements element
                    counter <- counter + 1
                    [ FSharpValue.MakeTuple([| box (sprintf "key-%d" counter); this.Value(types.[1], field, depth + 1) |], element) ]
                // `rngState.s`: a tuple of four state words.
                elif field = "S" then List.replicate 4 (this.Value(element, field, depth + 1))
                elif FSharpType.IsUnion element && element <> typeof<Json>
                     && not (element.IsGenericType && element.GetGenericTypeDefinition() = typedefof<list<_>>) then
                    [ for case in FSharpType.GetUnionCases element -> this.Case(case, depth + 1) ]
                else [ this.Value(element, field, depth + 1) ]
            let cases = FSharpType.GetUnionCases t
            let empty = FSharpValue.MakeUnion(cases |> Array.find (fun c -> c.Name = "Empty"), [||])
            let cons = cases |> Array.find (fun c -> c.Name = "Cons")
            List.foldBack (fun item tail -> FSharpValue.MakeUnion(cons, [| item; tail |])) items empty
        elif FSharpType.IsRecord t then
            let values = [| for f in FSharpType.GetRecordFields t -> this.Value(f.PropertyType, f.Name, depth + 1) |]
            FSharpValue.MakeRecord(t, values)
        elif FSharpType.IsUnion t then this.Case(FSharpType.GetUnionCases(t).[0], depth)
        else failwithf "kitchen sink: no value for %s (%s)" t.FullName field

    member this.Case(case: UnionCaseInfo, depth: int) : obj =
        FSharpValue.MakeUnion(case, [| for f in case.GetFields() -> this.Value(f.PropertyType, f.Name, depth + 1) |])

let private kitchenSink<'T> () : 'T = KitchenSink().Value(typeof<'T>, "", 0) :?> 'T

/// The kitchen-sink project: every field set, at the current schema version, with consistent
/// pack ids (packs merge only under valid ids).
let kitchenSinkProject () : GameProject =
    let project = kitchenSink<GameProject> ()
    let packs =
        project.ContentPacks
        |> List.mapi (fun i install ->
            let manifest = { install.Pack.Manifest with Id = sprintf "sink-pack-%d" i; Dependencies = [] }
            { install with Pack = { install.Pack with Manifest = manifest } })
    { project with SchemaVersion = 9.0; ContentPacks = packs }

let kitchenSinkContent () : GameContent = kitchenSink<GameContent> ()

[<Fact>]
let ``the kitchen sinks set every field and round-trip through the F# codecs`` () =
    let project = kitchenSinkProject ()
    Assert.Equal(project, decode SchemaJson.decodeGameProject (SchemaJson.encodeGameProject project))
    let content = kitchenSinkContent ()
    Assert.Equal(content, decode SchemaJson.decodeGameContent (SchemaJson.encodeGameContent content))
    // Every union case of the condition vocabulary is in there.
    Assert.Equal(FSharpType.GetUnionCases(typeof<EventCondition>).Length, project.Events.Head.Conditions.Length)

// ── Comparing the two sides ──────────────────────────────────────────────────

/// The first difference between two JSON trees, as `path: expected / actual`. Numbers may differ
/// by what the engine's fixed-point grids make of them (docs/NUMERICS.md: a chance on a 2⁻³²
/// grid reads 0.03 back as 0.030000000027939677, a speed of 4.5 tiles a second is 1843/8192 of
/// a tile a tick, 4.49951171875): up to 0.002, or a millionth of a large number.
let rec firstDifference (path: string) (expected: Json) (actual: Json) : string option =
    match expected, actual with
    | JNumber a, JNumber b ->
        if a = b || abs (a - b) <= max 0.002 (1e-6 * abs a) then None
        else Some(sprintf "%s: %s / %s" path (JsNumber.format a) (JsNumber.format b))
    | JObject a, JObject b ->
        let keys = (List.map fst a) @ (b |> List.map fst |> List.filter (fun key -> not (List.exists (fun (k, _) -> k = key) a)))
        keys
        |> List.tryPick (fun key ->
            match List.tryFind (fun (k, _) -> k = key) a, List.tryFind (fun (k, _) -> k = key) b with
            | Some(_, x), Some(_, y) -> firstDifference (path + "." + key) x y
            | Some(_, x), None -> Some(sprintf "%s.%s: %s / (absent)" path key (Json.stringify x))
            | None, Some(_, y) -> Some(sprintf "%s.%s: (absent) / %s" path key (Json.stringify y))
            | None, None -> None)
    | JArray a, JArray b when a.Length = b.Length ->
        List.zip a b |> List.indexed |> List.tryPick (fun (i, (x, y)) -> firstDifference (sprintf "%s[%d]" path i) x y)
    | _ when expected = actual -> None
    | _ ->
        let text (json: Json) = let s = Json.stringify json in if s.Length > 300 then s.Substring(0, 300) + "…" else s
        Some(sprintf "%s: %s / %s" path (text expected) (text actual))

// ── Rust-recorded content, compiled by F# ────────────────────────────────────

let private goldenContent () =
    Directory.GetFiles(repo [ "fixtures"; "golden"; "content" ], "*.json") |> Array.sort |> List.ofArray

[<Fact>]
let ``F# compiles the Rust content goldens to the content Rust recorded`` () =
    let failures =
        [ for file in goldenContent () do
              let fixture = parse (File.ReadAllText file)
              let name = Path.GetFileNameWithoutExtension file
              match ProjectLoad.migrateProject (Json.get "project" fixture) with
              | { Ok = true; Data = Some project } ->
                  let rust = parse (defaultArg (Json.asString (Json.get "stable" fixture)) "null")
                  let fsharp = parse (Json.stableStringify (SchemaJson.encodeGameContent (ContentCompiler.compile project)))
                  match firstDifference "content" rust fsharp with
                  | Some difference -> yield sprintf "%s (Rust / F#): %s" name difference
                  | None -> ()
              | result -> yield sprintf "%s: %s" name (String.concat "; " result.Errors) ]
    Assert.True(failures.IsEmpty, String.concat "\n" failures)

// ── F#-recorded pipeline output, checked by Rust ─────────────────────────────

let private loadFile (parts: string list) : GameProject =
    match ProjectLoad.migrateProjectText (File.ReadAllText(repo parts)) with
    | { Ok = true; Data = Some project } -> project
    | result -> failwithf "%A: %s" parts (String.concat "; " result.Errors)

let private withPack (project: GameProject) (file: string) : GameProject =
    let pack = decode SchemaJson.decodeContentPack (parse (File.ReadAllText(repo [ "fixtures"; "projects"; file ])))
    Document.run project (InstallPack pack)

/// The projects both pipelines must agree on (besides the content goldens above).
let private pipelineCases () : (string * GameProject) list =
    [ yield "kitchen-sink", kitchenSinkProject ()
      for id, project in templates () do
          yield "template-" + id, project
      for version in 1..8 do
          yield sprintf "project-v%d" version, loadFile [ "fixtures"; "projects"; sprintf "project-v%d.json" version ]
      yield "packs-project", loadFile [ "fixtures"; "projects"; "packs-project.json" ]
      yield "starter-demo-mod", withPack (starter ()) "demo-mod.json"
      yield "starter-my-first-mod", withPack (starter ()) "my-first-mod.json" ]

/// A section (content, start or presentation) with each top-level value the project carries
/// unchanged (the scene grids, mostly) written as `{"$project": key}`, which keeps the fixtures
/// small.
let private section (project: Json) (json: Json) : Json =
    match json with
    | JObject members ->
        JObject(
            members
            |> List.map (fun (key, value) ->
                match Json.tryGet key project with
                | Some same when same = value && Json.stringify value |> String.length > 200 -> key, JObject [ "$project", JString key ]
                | _ -> key, value)
        )
    | other -> other

let private record (name: string) (project: GameProject) : Json =
    let json = SchemaJson.encodeGameProject project
    JObject
        [ "name", JString name
          "project", json
          "content", section json (SchemaJson.encodeGameContent (ContentCompiler.compile project))
          "start", section json (CartridgeCompiler.StartSection project)
          "presentation", section json (CartridgeCompiler.PresentationSection project) ]

[<Fact>]
let ``the parity fixtures Rust checks are current`` () =
    let folder = repo [ "fixtures"; "parity" ]
    let recording = Environment.GetEnvironmentVariable "FARM_RECORD_PARITY" = "1"
    let files =
        [ yield "kitchen-sink-content.json", JObject [ "content", SchemaJson.encodeGameContent (kitchenSinkContent ()) ]
          for name, project in pipelineCases () do
              yield name + ".json", record name project ]
    if recording then
        Directory.CreateDirectory folder |> ignore
        for old in Directory.GetFiles(folder, "*.json") do
            File.Delete old
    let stale =
        [ for file, json in files do
              let path = Path.Combine(folder, file)
              let text = Json.stringify json + "\n"
              if recording then File.WriteAllText(path, text)
              elif not (File.Exists path) || File.ReadAllText path <> text then yield file ]
    Assert.True(
        stale.IsEmpty,
        sprintf "stale parity fixtures (rerun with FARM_RECORD_PARITY=1, then cargo test -p farm-sim --test fsharp_parity): %s" (String.concat ", " stale)
    )
