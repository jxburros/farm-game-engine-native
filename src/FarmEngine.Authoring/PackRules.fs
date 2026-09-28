namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open FarmEngine.Json
open FarmEngine.Schemas

/// Authoring rules from engine-core/packs.ts and engine-schemas/packs.ts.
/// The JSON adapter preserves extension fields while the schema records remain in C#.
module PackRules =
    [<Literal>]
    let EngineVersion = "0.5.0"

    let namespacedId (packId: string) (id: string) =
        if id.Contains ':' then id else packId + ":" + id

    /// Compatibility intentionally follows the web engine's small range language, including
    /// its major-only caret check for 0.x versions and ignored prerelease suffixes.
    let isEngineCompatible (range: string | null) (version: string) =
        let parse (value: string) =
            let m = Regex.Match(value.Trim(), @"^([0-9]+)\.([0-9]+)\.([0-9]+)")
            if not m.Success then None
            else Some(JsNumber.parse m.Groups[1].Value, JsNumber.parse m.Groups[2].Value, JsNumber.parse m.Groups[3].Value)
        let range = match range with null -> "*" | s -> s.Trim()
        if range = "" || range = "*" then true
        else
            match parse version with
            | None -> true
            | Some(a, b, c) ->
                let atLeast (x, y, z) = if a <> x then a > x elif b <> y then b > y else c >= z
                if range.StartsWith(">=", StringComparison.Ordinal) then
                    parse (range.Substring 2) |> Option.exists atLeast
                elif range.StartsWith '^' then
                    parse (range.Substring 1) |> Option.exists (fun (x, y, z) -> a = x && (if b <> y then b > y else c >= z))
                else parse range |> Option.exists (fun v -> v = (a, b, c))

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

    let private text (node: JsonNode | null) =
        match node with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Some(v.GetValue<string>())
        | _ -> None

    let private entries (node: JsonNode | null) =
        match node with :? JsonArray as a -> Seq.toList a | _ -> []

    let private field (key: string) (node: JsonNode | null) =
        match node with :? JsonObject as o -> o[key] | _ -> null

    let private copy (node: JsonNode | null) =
        match node with null -> null | n -> n.DeepClone()

    /// Namespace definitions and only the reference keys used by the reference engine.
    /// In particular sceneId, plugin source and embedded dialogue ids are not rewritten.
    let namespacePack (pack: ContentPack) : ContentPack =
        if pack.Manifest.Base then pack
        else
            let content = JsonSerializer.SerializeToNode(pack.Content, JsonDefaults.Options)
            let packId = pack.Manifest.Id
            let ids =
                [ for key in collections do yield! entries (field key content)
                  for npc in entries (field "npcs" content) do yield! entries (field "dialogue" npc) ]
                |> List.choose (field "id" >> text)
                |> List.filter (fun id -> not (id.Contains ':'))
                |> Set.ofList
            let reference (node: JsonNode | null) : JsonNode | null =
                match text node with
                | Some id when ids.Contains id -> JsonValue.Create(namespacedId packId id)
                | _ -> copy node
            let rec rewrite (node: JsonNode | null) : JsonNode | null =
                match node with
                | :? JsonArray as a -> JsonArray(a |> Seq.map rewrite |> Seq.toArray)
                | :? JsonObject as o ->
                    let result = JsonObject()
                    for KeyValue(key, value) in o do
                        result[key] <-
                            if referenceKeys.Contains key then
                                match text value with Some _ -> reference value | _ -> rewrite value
                            elif key = "prerequisites" then
                                match value with
                                | :? JsonArray as a -> JsonArray(a |> Seq.map reference |> Seq.toArray)
                                | _ -> rewrite value
                            else rewrite value
                    result
                | _ -> copy node
            let result =
                match content with :? JsonObject as o -> o | _ -> invalidOp "Pack content must be an object"
            for key in collections do
                match entries (field key result) with
                | [] -> ()
                | definitions ->
                    result[key] <- JsonArray(definitions |> List.map (fun definition ->
                        let rewritten = rewrite definition
                        match rewritten, text (field "id" rewritten) with
                        | (:? JsonObject as o), Some id -> o["id"] <- JsonValue.Create(namespacedId packId id)
                        | _ -> ()
                        rewritten) |> List.toArray)
            if not (isNull result["playerStart"]) then result["playerStart"] <- rewrite result["playerStart"]
            match result["strings"] with
            | :? JsonObject as locales ->
                let rewritten = JsonObject()
                for KeyValue(locale, table) in locales do
                    let translated = JsonObject()
                    match table with
                    | :? JsonObject as table ->
                        for KeyValue(key, value) in table do
                            let parts = key.Split ':'
                            let id = if parts.Length >= 3 then String.Join(":", parts[1 .. parts.Length - 2]) else ""
                            let key =
                                if parts.Length >= 3 && ids.Contains id then
                                    parts[0] + ":" + namespacedId packId id + ":" + parts[parts.Length - 1]
                                else key
                            translated[key] <- copy value
                        rewritten[locale] <- translated
                    | _ -> rewritten[locale] <- copy table
                result["strings"] <- rewritten
            | _ -> ()
            Records.withValue pack "Content" (box (result.Deserialize<PackContent>(JsonDefaults.Options)))

/// Diagnostics belong to authoring; the fallback engine retains its separate compatibility type.
type PackProblem = { PackId: string; Severity: string; Message: string }
