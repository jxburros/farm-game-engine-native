namespace FarmEngine.Authoring.Net

open System.Text.Json.Nodes
open FarmEngine.Authoring
open FarmEngine.Schemas

/// Project and exported-game loading for .NET callers that hold System.Text.Json nodes: the
/// Fable-safe `ProjectLoad` of the authoring core, with `JsonNode` in and out. Never throws.
module ProjectMigrations =
    /// The schema checks an exported game gets (`SchemaChecks.validateExportedGame`).
    let validateExportedGame (game: ExportedGame) : string list = SchemaChecks.validateExportedGame game

    /// Migrate raw project data up to the current schema version, parse and validate it.
    let migrateProjectJson (raw: Json) : MigrationResult<GameProject> = ProjectLoad.migrateProject raw

    /// A node `Json` cannot hold (a number beyond the double range, a duplicate key), as a
    /// failed result.
    let private unreadable (what: string) (message: string) : MigrationResult<'T> =
        { Ok = false; Data = None; FromVersion = 0.0; Migrated = false; Errors = [ what + " is not valid JSON: " + message ] }

    /// Migrate a raw project (from storage or import; null for JSON null). The node is not modified.
    let migrateProject (raw: JsonNode | null) : MigrationResult<GameProject> =
        match JsonInterop.tryOfNode raw with
        | Ok json -> ProjectLoad.migrateProject json
        | Error message -> unreadable "Project data" message

    /// `migrateProject` over JSON text.
    let migrateProjectText (json: string) : MigrationResult<GameProject> = ProjectLoad.migrateProjectText json

    /// Migrate raw exported-game data, parse and validate it.
    let migrateExportedGameJson (raw: Json) : MigrationResult<ExportedGame> = ProjectLoad.migrateExportedGame raw

    /// Migrate + validate an exported/shared game payload (null for JSON null).
    let migrateExportedGame (raw: JsonNode | null) : MigrationResult<ExportedGame> =
        match JsonInterop.tryOfNode raw with
        | Ok json -> ProjectLoad.migrateExportedGame json
        | Error message -> unreadable "Game data" message

    /// `migrateExportedGame` over JSON text.
    let migrateExportedGameText (json: string) : MigrationResult<ExportedGame> = ProjectLoad.migrateExportedGameText json

    /// A project as a System.Text.Json node (members in schema order).
    let toNode (project: GameProject) : JsonNode =
        match JsonInterop.toNode (ProjectLoad.toJson project) with
        | null -> JsonObject() :> JsonNode
        | node -> node
