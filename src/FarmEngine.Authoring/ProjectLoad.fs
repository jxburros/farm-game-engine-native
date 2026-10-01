namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// TS `MigrationResult<T>`: the migrated, parsed and validated value, or why there is none.
/// `FromVersion` is the schema version the data had; `Migrated` says a migration ran.
type MigrationResult<'T> =
    { Ok: bool
      Data: 'T option
      FromVersion: float
      Migrated: bool
      Errors: string list }

/// Loading projects and exported games (TS `migrateProject` / `migrateExportedGame`): the raw
/// migrations (`Migrations`), the typed parse (`SchemaJson`) and the schema checks
/// (`SchemaChecks`, at most 20 errors, like zod's `safeParse` shown in the editor). Never throws.
module ProjectLoad =
    let private failed (raw: RawMigrationResult) (errors: string list) : MigrationResult<'T> =
        { Ok = false; Data = None; FromVersion = raw.FromVersion; Migrated = raw.Migrated; Errors = errors }

    /// The typed parse and checks after the raw migrations; `prepare` runs on the parsed value
    /// before the checks. An exception from any step is an error in the result.
    let private finish (decode: Path -> Json -> 'T) (prepare: 'T -> 'T) (validate: 'T -> string list) (raw: RawMigrationResult) : MigrationResult<'T> =
        match raw.Data with
        | Some data when raw.Ok ->
            try
                match Decode.run decode data with
                | Error issue -> failed raw [ issue ]
                | Ok value ->
                    let value = prepare value
                    match List.truncate 20 (validate value) with
                    | [] -> { Ok = true; Data = Some value; FromVersion = raw.FromVersion; Migrated = raw.Migrated; Errors = [] }
                    | errors -> failed raw errors
            with error ->
                failed raw [ "Project data could not be read: " + error.Message ]
        | _ -> failed raw raw.Errors

    /// Migrate raw project data up to the current schema version, parse it and validate it.
    let migrateProject (raw: Json) : MigrationResult<GameProject> =
        Migrations.migrateProjectRaw raw |> finish SchemaJson.decodeGameProject DialogueCopies.reconcile SchemaChecks.validateProject

    /// `migrateProject` over JSON text.
    let migrateProjectText (text: string) : MigrationResult<GameProject> =
        match Json.parse text with
        | Ok raw -> migrateProject raw
        | Error message ->
            { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Project data is not valid JSON: " + message ] }

    /// Migrate raw exported-game data, parse it and validate it (`SchemaChecks.validateExportedGame`).
    let migrateExportedGame (raw: Json) : MigrationResult<ExportedGame> =
        Migrations.migrateExportedGameRaw raw |> finish SchemaJson.decodeExportedGame id SchemaChecks.validateExportedGame

    /// `migrateExportedGame` over JSON text.
    let migrateExportedGameText (text: string) : MigrationResult<ExportedGame> =
        match Json.parse text with
        | Ok raw -> migrateExportedGame raw
        | Error message ->
            { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Game data is not valid JSON: " + message ] }

    /// A project as the JSON the web version reads and writes (members in schema order).
    let toJson (project: GameProject) : Json = SchemaJson.encodeGameProject project

    /// A project as indented JSON text, for project files.
    let toText (project: GameProject) : string = Json.stringifyIndented (toJson project)
