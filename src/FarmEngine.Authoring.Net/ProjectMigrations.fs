namespace FarmEngine.Authoring.Net

open System.Text.Json
open System.Text.Json.Nodes
open FarmEngine.Authoring
open FarmEngine.Schemas

/// Project and exported-game migration on .NET: the F# migrations (`Migrations`) followed by
/// the typed parse and validation of FarmEngine.Schemas. The same results as the C#
/// `FarmEngine.Schemas.Migrations.MigrateProject` / `MigrateExportedGame`. Never throws.
module ProjectMigrations =

    let private failed (raw: RawMigrationResult) : MigrationResult<'T> =
        MigrationResult<'T>(
            Ok = false,
            FromVersion = raw.FromVersion,
            Migrated = raw.Migrated,
            Errors = System.Collections.Generic.List<string>(raw.Errors)
        )

    let private finish (parse: JsonObject -> float -> bool -> MigrationResult<'T>) (raw: RawMigrationResult) : MigrationResult<'T> =
        match raw.Data with
        | Some data when raw.Ok ->
            match JsonInterop.toNode data with
            | :? JsonObject as obj -> parse obj raw.FromVersion raw.Migrated
            | _ -> failed { raw with Ok = false; Errors = [ ": Expected object" ] }
        | _ -> failed raw

    /// Migrate raw project data (`Json`) up to the current schema version, then parse and
    /// validate it into a `GameProject`.
    let migrateProjectJson (raw: Json) : MigrationResult<GameProject> =
        Migrations.migrateProjectRaw raw
        |> finish (fun obj fromVersion migrated -> FarmEngine.Schemas.Migrations.ParseMigratedProject(obj, fromVersion, migrated))

    /// Migrate a raw project (from storage or import; null for JSON null) up to the current
    /// schema version and validate it. The caller's node is not modified.
    let migrateProject (raw: JsonNode | null) : MigrationResult<GameProject> = migrateProjectJson (JsonInterop.ofNode raw)

    /// `migrateProject` over JSON text.
    let migrateProjectText (json: string) : MigrationResult<GameProject> =
        match (try Ok(JsonInterop.ofNode (JsonNode.Parse json)) with :? JsonException as ex -> Error ex.Message) with
        | Ok raw -> migrateProjectJson raw
        | Error message ->
            failed { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Project data is not valid JSON: " + message ] }

    /// Migrate raw exported-game data (`Json`), then parse and validate it into an
    /// `ExportedGame`.
    let migrateExportedGameJson (raw: Json) : MigrationResult<ExportedGame> =
        Migrations.migrateExportedGameRaw raw
        |> finish (fun obj fromVersion migrated -> FarmEngine.Schemas.Migrations.ParseMigratedExportedGame(obj, fromVersion, migrated))

    /// Migrate + validate an exported/shared game payload (null for JSON null).
    let migrateExportedGame (raw: JsonNode | null) : MigrationResult<ExportedGame> = migrateExportedGameJson (JsonInterop.ofNode raw)

    /// `migrateExportedGame` over JSON text.
    let migrateExportedGameText (json: string) : MigrationResult<ExportedGame> =
        match (try Ok(JsonInterop.ofNode (JsonNode.Parse json)) with :? JsonException as ex -> Error ex.Message) with
        | Ok raw -> migrateExportedGameJson raw
        | Error message ->
            failed { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Game data is not valid JSON: " + message ] }
