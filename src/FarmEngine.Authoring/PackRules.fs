namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// Authoring rules from engine-core/packs.ts and engine-schemas/packs.ts.
module PackRules =
    [<Literal>]
    let EngineVersion = "0.5.0"

    let namespacedId (packId: string) (id: string) =
        if id.Contains ':' then id else packId + ":" + id

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// `^([0-9]+)\.([0-9]+)\.([0-9]+)` on the trimmed text: the three numbers, or `None`.
    let private parseVersion (value: string) : (float * float * float) option =
        let text = JsNumber.trim value
        let mutable i = 0
        let digits () =
            let start = i
            while i < text.Length && isDigit text.[i] do
                i <- i + 1
            if i > start then Some(JsNumber.parse (text.Substring(start, i - start))) else None
        let dot () =
            if i < text.Length && text.[i] = '.' then
                i <- i + 1
                true
            else
                false
        match digits () with
        | Some a when dot () ->
            match digits () with
            | Some b when dot () ->
                match digits () with
                | Some c -> Some(a, b, c)
                | None -> None
            | _ -> None
        | _ -> None

    /// Compatibility intentionally follows the web engine's small range language, including
    /// its major-only caret check for 0.x versions and ignored prerelease suffixes.
    let isEngineCompatible (range: string option) (version: string) =
        let range = match range with None -> "*" | Some s -> JsNumber.trim s
        if range = "" || range = "*" then true
        else
            match parseVersion version with
            | None -> true
            | Some(a, b, c) ->
                let atLeast (x, y, z) = if a <> x then a > x elif b <> y then b > y else c >= z
                if range.StartsWith(">=", StringComparison.Ordinal) then
                    parseVersion (range.Substring 2) |> Option.exists atLeast
                elif range.StartsWith("^", StringComparison.Ordinal) then
                    parseVersion (range.Substring 1) |> Option.exists (fun (x, y, z) -> a = x && (if b <> y then b > y else c >= z))
                else parseVersion range |> Option.exists (fun v -> v = (a, b, c))

    /// TS `PackManifestSchema.id`: `^[a-z0-9][a-z0-9-]*$`.
    let isValidPackId (id: string) : bool =
        let lowerOrDigit (c: char) = (c >= 'a' && c <= 'z') || isDigit c
        id.Length > 0 && lowerOrDigit id.[0] && id |> Seq.forall (fun c -> lowerOrDigit c || c = '-')

    /// Validate raw pack JSON (zod `safeParse` of `ContentPackSchema`). Never throws; errors are
    /// actionable paths. The manifest's required keys and id rule are checked first, then the
    /// first value of the wrong kind is reported.
    let validateContentPack (raw: Json) : Result<ContentPack, string list> =
        match raw with
        | JObject _ ->
            let errors =
                match Json.get "manifest" raw with
                | JObject _ as manifest ->
                    [ for key in [ "id"; "name"; "version" ] do
                          match Json.get key manifest with
                          | JString _ -> ()
                          | _ -> yield "manifest." + key + ": Required"
                      match Json.get "id" manifest with
                      | JString id when not (isValidPackId id) -> yield "manifest.id: pack ids must be lowercase letters, digits and dashes"
                      | _ -> () ]
                | _ -> [ "manifest: Required" ]
            if not (List.isEmpty errors) then Error errors
            else
                match Decode.run SchemaJson.decodeContentPack raw with
                | Ok pack -> Ok pack
                | Error message -> Error [ message ]
        | _ -> Error [ "Pack data is not an object — expected { manifest, content }" ]

    let private collections =
        [ "crops"; "items"; "recipes"; "machineTypes"; "nodeTypes"; "animalSpecies"
          "fishTables"; "weatherTypes"; "npcs"; "dialogues"; "scenes"; "events"
          "quests"; "shops"; "actions"; "minigames" ]

    let private referenceKeys =
        Set.ofList
            [ "itemId"; "cropType"; "machineTypeId"; "feedItemId"; "productItemId"
              "npcId"; "dialogueId"; "nextDialogueId"; "openShopId"; "offerQuestId"
              "questId"; "targetNPCId"; "targetCropType"; "shopId"; "giver"; "typeId"
              "speciesId"; "recipeId"; "nodeTypeId"; "weatherId"; "targetItemId"
              "requiredItemId"; "giftItemId"; "actionId"; "minigameId"; "useActionId" ]

    let private entries (json: Json) =
        match json with
        | JArray items -> items
        | _ -> []

    /// Namespace definitions and only the reference keys used by the reference engine.
    /// In particular sceneId, plugin source and embedded dialogue ids are not rewritten.
    let namespacePack (pack: ContentPack) : ContentPack =
        if pack.Manifest.Base then pack
        else
            let content = SchemaJson.encodePackContent pack.Content
            let packId = pack.Manifest.Id
            let ids =
                [ for key in collections do yield! entries (Json.get key content)
                  for npc in entries (Json.get "npcs" content) do yield! entries (Json.get "dialogue" npc) ]
                |> List.choose (Json.get "id" >> Json.asString)
                |> List.filter (fun id -> not (id.Contains ':'))
                |> Set.ofList
            let reference (json: Json) : Json =
                match json with
                | JString id when ids.Contains id -> JString(namespacedId packId id)
                | other -> other
            let rec rewrite (json: Json) : Json =
                match json with
                | JArray items -> JArray(List.map rewrite items)
                | JObject members ->
                    JObject(
                        members
                        |> List.map (fun (key, value) ->
                            key,
                            if referenceKeys.Contains key then
                                match value with
                                | JString _ -> reference value
                                | _ -> rewrite value
                            elif key = "prerequisites" then
                                match value with
                                | JArray items -> JArray(List.map reference items)
                                | _ -> rewrite value
                            else
                                rewrite value)
                    )
                | other -> other
            let definition (json: Json) =
                match rewrite json with
                | JObject members as rewritten ->
                    match Json.get "id" rewritten with
                    | JString id -> JObject(Json.setMember "id" (JString(namespacedId packId id)) members)
                    | _ -> rewritten
                | rewritten -> rewritten
            let strings (json: Json) =
                match json with
                | JObject locales ->
                    JObject(
                        locales
                        |> List.map (fun (locale, table) ->
                            match table with
                            | JObject members ->
                                locale,
                                JObject(
                                    members
                                    |> List.map (fun (key, value) ->
                                        let parts = key.Split ':'
                                        let id = if parts.Length >= 3 then String.Join(":", parts.[1 .. parts.Length - 2]) else ""
                                        if parts.Length >= 3 && ids.Contains id then
                                            parts.[0] + ":" + namespacedId packId id + ":" + parts.[parts.Length - 1], value
                                        else
                                            key, value)
                                )
                            | other -> locale, other)
                    )
                | other -> other
            let rewritten =
                Json.spread content
                |> List.map (fun (key, value) ->
                    if List.contains key collections then
                        match value with
                        | JArray [] -> key, value
                        | JArray items -> key, JArray(List.map definition items)
                        | _ -> key, value
                    elif key = "playerStart" && not (Json.isNullish value) then key, rewrite value
                    elif key = "strings" then key, strings value
                    else key, value)
            match Decode.run SchemaJson.decodePackContent (JObject rewritten) with
            | Ok content -> { pack with Content = content }
            | Error message -> invalidOp ("Pack content does not fit the schema after namespacing: " + message)

/// Diagnostics belong to authoring; the fallback engine retains its separate compatibility type.
type PackProblem = { PackId: string; Severity: string; Message: string }
