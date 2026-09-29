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

    /// Migrate a raw project (from storage or import; null for JSON null). The node is not modified.
    let migrateProject (raw: JsonNode | null) : MigrationResult<GameProject> = ProjectLoad.migrateProject (JsonInterop.ofNode raw)

    /// `migrateProject` over JSON text.
    let migrateProjectText (json: string) : MigrationResult<GameProject> = ProjectLoad.migrateProjectText json

    /// Migrate raw exported-game data, parse and validate it.
    let migrateExportedGameJson (raw: Json) : MigrationResult<ExportedGame> = ProjectLoad.migrateExportedGame raw

    /// Migrate + validate an exported/shared game payload (null for JSON null).
    let migrateExportedGame (raw: JsonNode | null) : MigrationResult<ExportedGame> = ProjectLoad.migrateExportedGame (JsonInterop.ofNode raw)

    /// `migrateExportedGame` over JSON text.
    let migrateExportedGameText (json: string) : MigrationResult<ExportedGame> = ProjectLoad.migrateExportedGameText json

    /// A project as a System.Text.Json node (members in schema order).
    let toNode (project: GameProject) : JsonNode =
        match JsonInterop.toNode (ProjectLoad.toJson project) with
        | null -> JsonObject() :> JsonNode
        | node -> node
