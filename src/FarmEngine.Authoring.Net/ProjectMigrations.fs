namespace FarmEngine.Authoring.Net

open System.Text.Json
open System.Text.Json.Nodes
open FarmEngine.Authoring
open FarmEngine.Json
open FarmEngine.Schemas

/// Project and exported-game migration on .NET: the F# migrations (`Migrations`), the typed
/// parse of FarmEngine.Schemas and the F# schema checks (`SchemaChecks`). The same results as
/// the C# `FarmEngine.Schemas.Migrations.MigrateProject` / `MigrateExportedGame`. Never throws.
module ProjectMigrations =
    /// The project the C# `SchemaValidation.ValidateExportedGame` checks for an exported game:
    /// the game's JSON round trip into a `GameProject` with fixed editor-only fields and, when it
    /// carries no player, a skeleton player. The round trip is what makes the result identical to
    /// the C# for every input: a `null` in a required field comes back as its default, a
    /// passthrough key such as `rngState` becomes a project field, NaN and mistyped keys throw.
    let private exportedGameAsProject (game: ExportedGame) : GameProject =
        let node =
            match JsonSerializer.SerializeToNode(game, JsonDefaults.Options) with
            | :? JsonObject as node -> node
            | _ -> failwith "an exported game serializes to a JSON object"
        node["id"] <- JsonValue.Create "exported-game"
        node["mode"] <- JsonValue.Create EditorModes.Play
        node["selectedTileType"] <- JsonValue.Create TileTypes.Grass
        if isNull game.Player then
            let player = JsonObject()
            player["direction"] <- JsonValue.Create Directions.Down
            player["sceneId"] <- JsonValue.Create "exported-game"
            node["player"] <- player
        match node.Deserialize<GameProject>(JsonDefaults.Options) with
        | null -> failwith "an exported game deserializes to a project"
        | project -> project

    /// The schema checks the migrations run on an exported game: `SchemaChecks` on the project
    /// the C# builds for it (`path: message`, all errors). The same list as the C#
    /// `SchemaValidation.ValidateExportedGame`, which it replaces on this path.
    let validateExportedGame (game: ExportedGame) : string list =
        SchemaChecks.exportedProjectIssues (not (isNull game.Player)) (exportedGameAsProject game) |> List.map string

    let private projectChecks =
        System.Func<GameProject, System.Collections.Generic.IReadOnlyList<string>>(fun project ->
            SchemaChecks.validateProject project |> Array.ofList :> _)

    let private exportedGameChecks =
        System.Func<ExportedGame, System.Collections.Generic.IReadOnlyList<string>>(fun game ->
            validateExportedGame game |> Array.ofList :> _)

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

    /// Migrate raw project data (`Json`) up to the current schema version, then parse it into a
    /// `GameProject` and validate it with `SchemaChecks.validateProject` (first 20 errors).
    let migrateProjectJson (raw: Json) : MigrationResult<GameProject> =
        Migrations.migrateProjectRaw raw
        |> finish (fun obj fromVersion migrated -> FarmEngine.Schemas.Migrations.ParseMigratedProject(obj, fromVersion, migrated, projectChecks))

    /// Migrate a raw project (from storage or import; null for JSON null) up to the current
    /// schema version and validate it. The caller's node is not modified.
    let migrateProject (raw: JsonNode | null) : MigrationResult<GameProject> = migrateProjectJson (JsonInterop.ofNode raw)

    /// `migrateProject` over JSON text.
    let migrateProjectText (json: string) : MigrationResult<GameProject> =
        match (try Ok(JsonInterop.ofNode (JsonNode.Parse json)) with :? JsonException as ex -> Error ex.Message) with
        | Ok raw -> migrateProjectJson raw
        | Error message ->
            failed { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Project data is not valid JSON: " + message ] }

    /// Migrate raw exported-game data (`Json`), then parse it into an `ExportedGame` and validate
    /// it with `validateExportedGame` (first 20 errors).
    let migrateExportedGameJson (raw: Json) : MigrationResult<ExportedGame> =
        Migrations.migrateExportedGameRaw raw
        |> finish (fun obj fromVersion migrated -> FarmEngine.Schemas.Migrations.ParseMigratedExportedGame(obj, fromVersion, migrated, exportedGameChecks))

    /// Migrate + validate an exported/shared game payload (null for JSON null).
    let migrateExportedGame (raw: JsonNode | null) : MigrationResult<ExportedGame> = migrateExportedGameJson (JsonInterop.ofNode raw)

    /// `migrateExportedGame` over JSON text.
    let migrateExportedGameText (json: string) : MigrationResult<ExportedGame> =
        match (try Ok(JsonInterop.ofNode (JsonNode.Parse json)) with :? JsonException as ex -> Error ex.Message) with
        | Ok raw -> migrateExportedGameJson raw
        | Error message ->
            failed { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ "Game data is not valid JSON: " + message ] }
