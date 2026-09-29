namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// Keep changes: a playtest's final game state written back into the project (TS
/// `applyStateToProject`, which the Rust player also implements for project-started players).
/// The editor's Play Mode runs a compiled cartridge, so the state comes back as the engine's
/// `GameState` JSON and F# applies it (docs/LANGUAGES.md, `State.cs ApplyStateToProject`).
module Playtest =
    let private objectKey (key: string) (value: Json) : Json option =
        match Json.tryGet key value with
        | Some(JObject _ as found) -> Some found
        | _ -> None

    /// Sets each `(key, value)` in order (a missing value removes the key).
    let private setAll (pairs: (string * Json option) list) (target: Json) : Json =
        pairs
        |> List.fold
            (fun acc (key, value) ->
                match value with
                | Some v -> Json.set key v acc
                | None -> Json.remove key acc)
            target

    let private npc (npcs: Json) (entry: Json) : Json =
        match Json.asString (Json.get "id" entry) |> Option.bind (fun id -> objectKey id npcs) with
        | Some state ->
            entry
            |> setAll [ "x", Some(Json.get "x" state); "y", Some(Json.get "y" state); "sceneId", Some(Json.get "sceneId" state) ]
        | None -> entry

    let private quest (quests: Json) (entry: Json) : Json =
        match Json.asString (Json.get "id" entry) |> Option.bind (fun id -> objectKey id quests) with
        | None -> entry
        | Some progress ->
            let withStatus = Json.set "status" (Json.get "status" progress) entry
            match objectKey "objectives" progress, Json.get "objectives" withStatus with
            | Some objectives, JArray items ->
                let objective (item: Json) =
                    match Json.asString (Json.get "id" item) |> Option.bind (fun id -> objectKey id objectives) with
                    | Some state ->
                        item |> setAll [ "progress", Some(Json.get "progress" state); "completed", Some(Json.get "completed" state) ]
                    | None -> item
                Json.set "objectives" (JArray(List.map objective items)) withStatus
            | _ -> withStatus

    let private mapArray (key: string) (f: Json -> Json) (value: Json) : Json =
        match Json.get key value with
        | JArray items -> Json.set key (JArray(List.map f items)) value
        | _ -> value

    /// The project JSON with the state written back.
    let applyStateJson (project: Json) (state: Json) : Json =
        let player = Json.get "player" state
        let clock = Json.get "clock" state
        let flags =
            match Json.get "flags" state with
            | JObject members -> JObject(members |> List.map (fun (key, value) -> key, JBool(Json.truthy value)))
            | _ -> JObject []
        let projectPlayer =
            Json.get "player" project
            |> setAll
                ([ for key in [ "x"; "y"; "direction"; "sceneId"; "inventory"; "maxInventorySize"; "money"; "energy"; "maxEnergy"; "skills"; "activeQuests"; "completedQuests" ] ->
                       key, Some(Json.get key player) ]
                 @ [ "equippedTool", Json.tryGet "equippedTool" player |> Option.filter (Json.isNullish >> not) ])
        project
        |> Json.set "scenes" (Json.get "scenes" (Json.get "world" state))
        |> mapArray "npcs" (npc (Json.get "npcs" state))
        |> mapArray "quests" (quest (Json.get "quests" state))
        |> Json.set "player" projectPlayer
        |> setAll
            [ "eventFlags", Some flags
              "animals", Some(Json.get "animals" state)
              "socialState", Some(Json.get "social" state)
              "quarantinedItems", Some(Json.get "quarantinedItems" state)
              "currentWeatherId", Some(Json.get "weatherId" clock)
              "mineDeepestFloor", Some(Json.get "deepestFloor" (Json.get "mine" state))
              "currentDay", Some(Json.get "day" clock)
              "currentSeason", Some(Json.get "season" clock)
              "currentTimeMinutes", Some(Json.get "timeMinutes" clock)
              "currentYear", Some(Json.get "year" clock)
              "rngState", Some(Json.get "rng" state) ]

    /// The project with a playtest's final state (the engine's `GameState` JSON) written back,
    /// or why the result is not a valid project.
    let applyState (project: GameProject) (state: Json) : Result<GameProject, string> =
        match state with
        | JObject _ -> Decode.run SchemaJson.decodeGameProject (applyStateJson (SchemaJson.encodeGameProject project) state)
        | _ -> Error "The game state is not an object."
