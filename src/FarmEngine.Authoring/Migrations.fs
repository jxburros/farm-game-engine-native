namespace FarmEngine.Authoring

// Port of packages/engine-schemas/src/migrations.ts (by way of the C#
// src/FarmEngine.Schemas/Migrations.cs, whose behaviour on malformed data it keeps: JS
// TypeErrors become "Migration failed: …" errors instead of exceptions).
//
// Versioned, pure project migrations. Every migration is a pure `(vN) => vN+1` function over
// raw JSON (`Json`). They never touch wall-clock time, RNG or I/O, so the same input always
// yields the same output. This file stops at the migrated raw JSON: parsing it into the typed
// schema and validating it happens in `ProjectLoad`.

/// The raw half of the TS `MigrationResult<T>`: `Data` is the migrated JSON, before the typed
/// parse and validation. `Ok` is false when the data could not be migrated at all.
type RawMigrationResult =
    { Ok: bool
      Data: Json option
      FromVersion: float
      Migrated: bool
      Errors: string list }

/// Project migrations v1 → v9 (TS `MIGRATIONS` up to v8, `detectProjectVersion`, and the
/// migration halves of `migrateProject` / `migrateExportedGame`; v8 → v9 is docs/NUMERICS.md).
module Migrations =

    /// The current project schema (TS `CURRENT_PROJECT_SCHEMA_VERSION` was 8; v9 is the native
    /// numerics grid, docs/NUMERICS.md).
    [<Literal>]
    let CurrentProjectSchemaVersion = 9.0

    /// Default weather set every pre-v6 project receives (tunable afterwards). TS
    /// `defaultWeatherConfig`.
    let defaultWeatherConfig () : Json =
        let weatherType id name waters damage inside overlay =
            JObject
                [ "id", JString id
                  "name", JString name
                  "watersOutdoorSoil", JBool waters
                  "cropDamageChance", JNumber damage
                  "npcsStayInside", JBool inside
                  "overlay", (match overlay with Some o -> JString o | None -> JNull) ]
        let entry weatherId weight = JObject [ "weatherId", JString weatherId; "weight", JNumber weight ]
        JObject
            [ "types",
              JArray
                  [ weatherType "sun" "Sunny" false 0.0 false None
                    weatherType "rain" "Rain" true 0.0 false (Some "rain")
                    weatherType "storm" "Storm" true 0.03 true (Some "rain")
                    weatherType "snow" "Snow" false 0.0 false (Some "snow") ]
              "table",
              JObject
                  [ "spring", JArray [ entry "sun" 6.0; entry "rain" 3.0; entry "storm" 1.0 ]
                    "summer", JArray [ entry "sun" 7.0; entry "rain" 1.0; entry "storm" 2.0 ]
                    "fall", JArray [ entry "sun" 6.0; entry "rain" 3.0; entry "storm" 1.0 ]
                    "winter", JArray [ entry "sun" 5.0; entry "snow" 5.0 ] ] ]

    let private emptyArray () = JArray []
    let private emptyObject () = JObject []
    let private str (s: string) () = JString s
    let private num (n: float) () = JNumber n

    /// Applies `{ ...value, k1: value.k1 ?? f1(), k2: … }` in literal order.
    let private withDefaults (defaults: (string * (unit -> Json)) list) (value: Json) : Json =
        defaults |> List.fold (fun acc (key, fallback) -> Json.withDefault key fallback acc) value

    /// `scene.tiles.map(row => row.map(f))` for every scene, with JS error paths.
    let private mapTiles (scenePath: string) (f: Json -> Json) (scene: Json) : Json =
        let result = JObject(Json.spread scene)
        let tilesPath = scenePath + ".tiles"
        let tiles = Json.get "tiles" result |> Json.mapArray tilesPath (fun rowPath row -> Json.mapArray rowPath (fun _ tile -> f tile) row)
        Json.set "tiles" tiles result

    // ── v1 → v2 ────────────────────────────────────────────────────────────

    /// Classify a tile type into its render layer (duplicated from engine rules on purpose —
    /// migrations must be frozen in time, not track live code).
    let private classifyTileTypeV2 (tileType: Json) : string =
        match Json.asString tileType with
        | Some "path" -> "overlay"
        | Some "wall"
        | Some "door" -> "object"
        | _ -> "background"

    let private migrateTileToLayers (tile: Json) : Json =
        if not (Json.isNullish (Json.get "background" (Json.require tile))) then
            tile
        else
            let tileType = Json.get "type" tile
            let layer = classifyTileTypeV2 tileType
            tile
            |> Json.set "background" (if layer = "background" then tileType else JString "grass")
            |> Json.set "overlay" (if layer = "overlay" then tileType else JNull)
            |> Json.set "object" (if layer = "object" then tileType else JNull)

    /// v1 → v2: introduce layered tiles (background/overlay/object).
    let migrateV1ToV2 (project: Json) : Json =
        let scenes = Json.get "scenes" project |> Json.mapArray "$.scenes" (fun path scene -> mapTiles path migrateTileToLayers scene)
        Json.set "scenes" scenes project

    // ── v2 → v3 ────────────────────────────────────────────────────────────

    /// Backfill scene/tile fields older data may lack.
    let private backfillScenes (scenes: Json) : Json =
        scenes
        |> Json.mapArray "$.scenes" (fun path scene ->
            JObject(Json.spread scene)
            |> withDefaults [ "transitions", emptyArray; "npcs", emptyArray; "events", emptyArray ]
            |> mapTiles path (fun tile ->
                let t = JObject(Json.spread (Json.require tile))
                t
                |> withDefaults
                    [ "collision", (fun () -> JBool(Json.asString (Json.get "object" t) = Some "wall"))
                      "soilMoisture", num 0.0
                      "soilFertility", num 0.0 ]))

    /// v2 → v3: make every optional-in-practice field concrete and stamp schemaVersion.
    /// Replaces the ad-hoc backfill effects from App.tsx.
    let migrateV2ToV3 (project: Json) : Json =
        // The TS object literal reads the ORIGINAL project, which `project` still is here.
        let firstSceneId = project |> Json.get "scenes" |> Json.index 0 |> Json.get "id"
        let player =
            JObject(Json.spread (Json.get "player" project |> Json.orElse (JObject [])))
            |> withDefaults
                [ "direction", str "down"
                  "inventory", emptyArray
                  "maxInventorySize", num 20.0
                  "money", num 0.0
                  "activeQuests", emptyArray
                  "completedQuests", emptyArray
                  "pixelX", num 0.0
                  "pixelY", num 0.0
                  "targetX", num 0.0
                  "targetY", num 0.0 ]
        project
        |> Json.set "scenes" (backfillScenes (Json.get "scenes" project))
        |> withDefaults
            [ "id", str "project-1"
              "name", str "My Farming Game"
              "version", str "2.0"
              "npcs", emptyArray
              "items", emptyArray
              "events", emptyArray
              "dialogues", emptyArray
              "quests", emptyArray
              "eventFlags", emptyObject
              "startSceneId", (fun () -> firstSceneId |> Json.orElse (JString "scene-farm"))
              "mode", str "play"
              "selectedTileType", str "grass"
              "selectedNPCId", (fun () -> JNull)
              "selectedItemId", (fun () -> JNull)
              "currentTime", num 0.0
              "customAssets", emptyArray
              "currentSeason", str "spring"
              "currentDay", num 1.0
              "gameStartTime", (fun () -> Json.get "currentTime" project |> Json.orElse (JNumber 0.0)) ]
        |> Json.set "player" player

    // ── v3 → v4 ────────────────────────────────────────────────────────────

    /// Convert a legacy ms growth duration into whole in-game days. 5 real seconds ≈ 1 in-game
    /// day for prototype-era timings, clamped to [1, 28].
    let private msToGrowthDays (growthTimeMs: float) : float =
        System.Math.Min(28.0, System.Math.Max(1.0, JsNumber.round (growthTimeMs / 5000.0)))

    /// v3 → v4 (M2): day-based game time.
    /// - custom crop definitions gain growthDays/regrowthDays (converted from ms)
    /// - planted crops gain plantedOnDay/daysGrown (approximated from their current wall-clock
    ///   stage so in-progress farms keep their progress)
    /// - projects gain shops, nodeTypes, settings, currentTimeMinutes, currentYear and player
    ///   energy
    let migrateV3ToV4 (project: Json) : Json =
        let currentDay = Json.get "currentDay" project |> Json.orElse (JNumber 1.0)
        let currentTime = Json.get "currentTime" project |> Json.orElse (JNumber 0.0) |> Json.toNumber

        // Keyed like the TS `cropDefLookup[crop.id] = crop` (the key is coerced to a string, the
        // last definition wins) and holding the ORIGINAL definitions.
        let cropDefLookup =
            Json.get "customCrops" project
            |> Json.elements "$.customCrops"
            |> List.fold (fun (lookup: Map<string, Json>) crop -> lookup.Add(Json.propertyKey "id" (Json.require crop), crop)) Map.empty

        let numberOr (fallback: float) (value: Json) = value |> Json.orElse (JNumber fallback) |> Json.toNumber

        let migrateCrop (crop: Json) : Json =
            if not (Json.truthy crop) || Json.has "plantedOnDay" crop then
                crop
            else
                let def = cropDefLookup.TryFind(Json.propertyKey "type" crop) |> Option.defaultValue JNull
                let growthTime = Json.get "growthTime" def |> numberOr 15000.0
                let stages = Json.get "stages" def |> numberOr 4.0
                let growthDays =
                    match Json.get "growthDays" def with
                    | JNull -> msToGrowthDays growthTime
                    | gd -> Json.toNumber gd
                // Recover the legacy wall-clock stage, then express it as watered days.
                let plantedAt =
                    match Json.get "plantedAt" crop with
                    | JNull -> currentTime
                    | pa -> Json.toNumber pa
                let elapsed = System.Math.Max(0.0, currentTime - plantedAt)
                let stageTime = growthTime / stages
                let waterPenalty = if Json.truthy (Json.get "watered" crop) then 1.0 else 0.5
                let legacyStage = System.Math.Min(System.Math.Floor((elapsed * waterPenalty) / stageTime), stages - 1.0)
                let daysGrown =
                    System.Math.Min(growthDays, System.Math.Floor((legacyStage / System.Math.Max(1.0, stages - 1.0)) * growthDays))
                crop
                |> Json.set "plantedOnDay" currentDay
                |> Json.set "daysGrown" (JNumber daysGrown)
                |> Json.set "stage" (JNumber legacyStage)

        let migrateCropDef (def: Json) : Json =
            let result =
                JObject(Json.spread (Json.require def))
                |> Json.withDefault "growthDays" (fun () -> JNumber(msToGrowthDays (Json.get "growthTime" def |> numberOr 15000.0)))
            if not (Json.isNullish (Json.get "regrowthDays" result)) then
                result
            elif Json.truthy (Json.get "canRegrow" result) && Json.truthy (Json.get "regrowthTime" result) then
                result |> Json.set "regrowthDays" (JNumber(msToGrowthDays (Json.toNumber (Json.get "regrowthTime" result))))
            else
                // regrowthDays: undefined — dropped on serialization.
                result |> Json.remove "regrowthDays"

        let migrateTile (tile: Json) : Json =
            let crop = Json.get "crop" (Json.require tile)
            if Json.truthy crop then Json.set "crop" (migrateCrop crop) tile else tile

        let withPlayer (p: Json) =
            // Exported games have no player object — only touch it when present.
            let player = Json.get "player" p
            if Json.truthy player then
                let energized = player |> withDefaults [ "energy", num 100.0; "maxEnergy", num 100.0 ]
                Json.set "player" energized p
            else
                p

        project
        |> Json.set "customCrops" (Json.get "customCrops" project |> Json.mapArray "$.customCrops" (fun _ def -> migrateCropDef def))
        |> Json.set "scenes" (Json.get "scenes" project |> Json.mapArray "$.scenes" (fun path scene -> mapTiles path migrateTile scene))
        |> withDefaults
            [ "currentTimeMinutes", num (6.0 * 60.0)
              "currentYear", num 1.0
              "shops", emptyArray
              "nodeTypes", emptyArray
              "settings", emptyObject ]
        |> withPlayer

    // ── v4 → v5 ────────────────────────────────────────────────────────────

    /// v4 → v5 (M3): events gain trigger + conditions; legacy
    /// triggerType/triggerX/requiredItem/requiredFlag/triggerTime fields are converted. (The
    /// legacy event model was never evaluated at runtime, so this conversion is best-effort by
    /// declared intent.)
    let private migrateEventV4 (ev: Json) : Json =
        let ev = Json.require ev
        if Json.has "trigger" ev && Json.has "conditions" ev then
            ev
        else
            let trigger, conditions =
                match Json.asString (Json.get "triggerType" ev) with
                | Some "location" ->
                    "enter",
                    [ if Json.has "triggerX" ev && Json.has "triggerY" ev then
                          JObject [ "type", JString "enterTile"; "x", Json.get "triggerX" ev; "y", Json.get "triggerY" ev ] ]
                | Some "item" ->
                    "tick",
                    [ if Json.truthy (Json.get "requiredItem" ev) then
                          JObject [ "type", JString "hasItem"; "itemId", Json.get "requiredItem" ev; "quantity", JNumber 1.0 ] ]
                | Some "flag" ->
                    "tick",
                    [ if Json.truthy (Json.get "requiredFlag" ev) then
                          JObject [ "type", JString "flag"; "flag", Json.get "requiredFlag" ev; "value", JBool true ] ]
                | Some "time" ->
                    "tick",
                    [ if Json.has "triggerTime" ev then
                          let time = Json.get "triggerTime" ev
                          JObject [ "type", JString "timeOfDay"; "minMinute", time; "maxMinute", Json.add time 60.0 ] ]
                | _ -> "enter", []
            ev
            |> Json.set "trigger" (JString trigger)
            |> Json.set "conditions" (JArray conditions)
            |> withDefaults [ "outcomes", emptyArray; "active", (fun () -> JBool true); "repeatable", (fun () -> JBool false) ]

    let migrateV4ToV5 (project: Json) : Json =
        Json.set "events" (Json.get "events" project |> Json.mapArray "$.events" (fun _ ev -> migrateEventV4 ev)) project

    // ── v5 → v8 ────────────────────────────────────────────────────────────

    /// v5 → v6 (M4): recipes, machines, weather, animals, fishing, mining, skills — all
    /// backfilled with sensible empty/default content.
    let migrateV5ToV6 (project: Json) : Json =
        project
        |> withDefaults
            [ "recipes", emptyArray
              "machineTypes", emptyArray
              "weather", defaultWeatherConfig
              "animalSpecies", emptyArray
              "animals", emptyArray
              "fishTables", emptyArray
              "mine", (fun () -> JObject [ "enabled", JBool false ]) ]

    /// v6 → v7 (M5): installed content packs live in the project.
    let migrateV6ToV7 (project: Json) : Json = project |> Json.withDefault "contentPacks" emptyArray

    /// v7 → v8: graphics settings (pixel-art rendering on by default).
    let migrateV7ToV8 (project: Json) : Json =
        project |> Json.withDefault "graphics" (fun () -> JObject [ "pixelArt", JBool true ])

    /// A number moved to the nearest multiple of `1 / scale` (other JSON stays as it is).
    let private onGrid (scale: float) (value: Json) : Json =
        match value with
        | JNumber n when not (System.Double.IsNaN n || System.Double.IsInfinity n) -> JNumber(JsNumber.roundHalfAway (n * scale) / scale)
        | other -> other

    let private gridMembers (grids: (string * float) list) (value: Json) : Json =
        match value with
        | JObject _ ->
            grids |> List.fold (fun acc (key, scale) -> if Json.has key acc then Json.set key (onGrid scale (Json.get key acc)) acc else acc) value
        | other -> other

    let private gridEach (key: string) (grids: (string * float) list) (project: Json) : Json =
        match Json.get key project with
        | JArray items -> Json.set key (JArray(List.map (gridMembers grids) items)) project
        | _ -> project

    /// Positions in 1/8192 tile, energy in 1/1000 point, the time of day in 1/1000000 minute.
    let private positionGrid = 8192.0

    /// v8 → v9 (docs/NUMERICS.md): the values a project carries into play move to the integer
    /// grid the engine keeps them on, so the editor shows what the game runs. Content
    /// definitions keep what the creator typed (Problems warns where the engine rounds).
    let migrateV8ToV9 (project: Json) : Json =
        let project =
            match Json.get "player" project with
            | JObject _ as player ->
                Json.set
                    "player"
                    (gridMembers [ "x", positionGrid; "y", positionGrid; "money", 1.0; "energy", 1000.0; "maxEnergy", 1000.0 ] player)
                    project
            | _ -> project
        let project =
            if Json.has "currentTimeMinutes" project then
                Json.set "currentTimeMinutes" (onGrid 1_000_000.0 (Json.get "currentTimeMinutes" project)) project
            else
                project
        project
        |> gridEach "npcs" [ "x", positionGrid; "y", positionGrid ]
        |> gridEach "animals" [ "x", positionGrid; "y", positionGrid; "mood", 1.0 ]

    // ── Pipeline ───────────────────────────────────────────────────────────

    /// Registry of migrations (TS `MIGRATIONS`): `registry.[n]` upgrades a version-n project to
    /// version n+1.
    let registry: Map<float, Json -> Json> =
        Map.ofList
            [ 1.0, migrateV1ToV2
              2.0, migrateV2ToV3
              3.0, migrateV3ToV4
              4.0, migrateV4ToV5
              5.0, migrateV5ToV6
              6.0, migrateV6ToV7
              7.0, migrateV7ToV8
              8.0, migrateV8ToV9 ]

    /// Detect the schema version of a raw (possibly legacy) project object.
    let detectProjectVersion (raw: Json) : float =
        if not (Json.isObjectLike raw) then
            CurrentProjectSchemaVersion
        else
            match Json.get "schemaVersion" raw with
            | JNumber v -> v
            | _ ->
                let firstTile = raw |> Json.get "scenes" |> Json.index 0 |> Json.get "tiles" |> Json.index 0 |> Json.index 0
                if Json.truthy firstTile && not (Json.isNullish (Json.get "background" firstTile)) then 2.0 else 1.0

    let private failure (fromVersion: float) (migrated: bool) (error: string) : RawMigrationResult =
        { Ok = false
          Data = None
          FromVersion = fromVersion
          Migrated = migrated
          Errors = [ error ] }

    /// Runs the registry from `fromVersion` up to the current version. `Error` carries the
    /// "No migration registered" message.
    let private runRegistry (fromVersion: float) (start: Json) : Result<Json, string> =
        let rec loop (v: float) (data: Json) =
            if v >= CurrentProjectSchemaVersion then
                Ok data
            else
                match registry.TryFind v with
                | Some step -> loop (v + 1.0) (step data)
                | None -> Error("No migration registered for schema version " + JsNumber.format v)
        loop fromVersion start

    /// The shared shape of `migrateProject` and `migrateExportedGame` up to the typed parse.
    /// `finish original migrated` post-processes the migrated data.
    let private migrate (noun: string) (finish: Json -> Json -> Json) (raw: Json) : RawMigrationResult =
        if not (Json.isObjectLike raw) then
            failure 0.0 false (noun + " data is not an object")
        else
            let fromVersion = detectProjectVersion raw
            let current = CurrentProjectSchemaVersion
            if fromVersion > current then
                failure
                    fromVersion
                    false
                    (noun + " schema version " + JsNumber.format fromVersion + " is newer than this engine supports ("
                     + JsNumber.format current + "). Update the engine.")
            else
                let migrated = fromVersion < current
                // `raw` may be an array: the migrations see `{ ...raw }`.
                let original = JObject(Json.spread raw)
                try
                    match runRegistry fromVersion original with
                    | Error message -> failure fromVersion false message
                    | Ok data ->
                        let result = finish original data |> Json.set "schemaVersion" (JNumber current)
                        { Ok = true
                          Data = Some result
                          FromVersion = fromVersion
                          Migrated = migrated
                          Errors = [] }
                with
                | JsTypeError message -> failure fromVersion migrated ("Migration failed: " + message)

    /// Migrate a raw project object (from storage or import) up to the current schema version.
    /// Never throws. `Data` still needs the typed parse and validation of `migrateProject`.
    let migrateProjectRaw (raw: Json) : RawMigrationResult = migrate "Project" (fun _ data -> data) raw

    /// Editor-only project fields an exported game must not gain.
    let private editorOnlyKeys =
        [ "id"; "mode"; "selectedTileType"; "selectedNPCId"; "selectedItemId"; "currentTime"; "eventFlags" ]

    /// Migrate an exported/shared game payload. Exported games ride the SAME migration registry
    /// as projects; afterwards the fields project migrations synthesize (editor-only fields and
    /// a skeleton player) are dropped unless the original had them. Never throws.
    let migrateExportedGameRaw (raw: Json) : RawMigrationResult =
        let finish (original: Json) (data: Json) =
            ("player" :: editorOnlyKeys)
            |> List.fold (fun acc key -> if Json.has key original then acc else Json.remove key acc) data
        migrate "Game" finish raw
