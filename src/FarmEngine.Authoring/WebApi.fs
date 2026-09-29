/// The authoring core for JavaScript hosts (the web editor, via Fable): plain functions over
/// JSON text, so callers never see the F# records. Every function returns JSON text or bytes and
/// reports failures in its result instead of throwing (except `compileCartridge`, which, like
/// `CartridgeCompiler.Compile`, refuses a project whose Problems include errors).
module FarmEngine.Authoring.WebApi

open FarmEngine.Schemas

let private text (json: Json) = Json.stringify json

let private strings (items: string list) = JArray(List.map JString items)

let private migration (encode: 'T -> Json) (result: MigrationResult<'T>) : string =
    JObject
        [ "ok", JBool result.Ok
          "fromVersion", JNumber result.FromVersion
          "migrated", JBool result.Migrated
          "errors", strings result.Errors
          "data", (match result.Data with Some data -> encode data | None -> JNull) ]
    |> text

/// `{ ok, fromVersion, migrated, errors, data }` for stored or imported project JSON: migrated
/// to the current schema, parsed and validated (`data` is null when refused).
let migrateProject (projectJson: string) : string =
    migration SchemaJson.encodeGameProject (ProjectLoad.migrateProjectText projectJson)

/// The same for an exported (shared) game.
let migrateExportedGame (gameJson: string) : string =
    migration SchemaJson.encodeExportedGame (ProjectLoad.migrateExportedGameText gameJson)

/// A current-schema project, or the first issue (the typed parse without migration).
let private project (projectJson: string) : Result<GameProject, string> =
    match Json.parse projectJson with
    | Error message -> Error message
    | Ok json -> Decode.run SchemaJson.decodeGameProject json

/// `{ ok, errors, data }` around a function of a project.
let private withProject (f: GameProject -> Json) (projectJson: string) : string =
    match project projectJson with
    | Ok parsed -> JObject [ "ok", JBool true; "errors", JArray []; "data", f parsed ]
    | Error issue -> JObject [ "ok", JBool false; "errors", strings [ issue ]; "data", JNull ]
    |> text

let private problem (p: Problem) : Json =
    JObject
        [ "severity", JString p.SeverityName
          "code", JString p.Code
          "path", JString p.Path
          "message", JString p.Message
          "targetKind", JString p.TargetKind
          "targetId", (match p.TargetId with null -> JNull | id -> JString id)
          "targetX", JNumber(float p.TargetX)
          "targetY", JNumber(float p.TargetY) ]

/// `{ ok, errors, data: Problem[] }`: the Problems panel for a current-schema project.
let problems (projectJson: string) : string =
    withProject (fun p -> JArray(Problems.collect p |> List.map problem)) projectJson

/// `{ ok, errors, data: GameContent }`: the content the game runs on (built-ins, packs, locale).
let compileContent (projectJson: string) : string =
    withProject (ContentCompiler.compile >> SchemaJson.encodeGameContent) projectJson

/// The `.farmcart` bytes of a current-schema project. Throws when the project does not parse or
/// Problems reports errors.
let compileCartridge (projectJson: string) : byte[] =
    match project projectJson with
    | Ok parsed -> CartridgeCompiler.Compile parsed
    | Error issue -> failwith issue

/// A new project from a template ("starter", "blank", "cozy", "quest"), as JSON.
let createProject (template: string) (now: float) : string =
    ProjectCatalog.CreateProjectForTemplate(template, now) |> ProjectLoad.toJson |> text

/// Stable JSON (sorted keys, JS number formatting) of any JSON text: the text hashes compare.
let stableJson (json: string) : string =
    match Json.parse json with
    | Ok value -> Json.stableStringify value
    | Error message -> failwith message
