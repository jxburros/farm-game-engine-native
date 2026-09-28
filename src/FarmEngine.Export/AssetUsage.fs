namespace FarmEngine.Export

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open FarmEngine.Json
open FarmEngine.Schemas

/// Which custom assets the game uses (docs/EXPORT.md "Only used assets ship").
module AssetUsage =
    /// Every string value in a JSON tree.
    let rec private strings (found: HashSet<string>) (node: JsonNode | null) =
        match node with
        | :? JsonObject as o ->
            for KeyValue(_, value) in o do
                strings found value
        | :? JsonArray as a ->
            for value in a do
                strings found value
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> found.Add(v.GetValue<string>()) |> ignore
        | _ -> ()

    /// Assets nothing in the game refers to. An asset counts as used when any value in the
    /// project outside the asset list names its id or its data URL (visual bindings, legacy
    /// custom images, crop art, pack content, the export icon), when it is the tile art for a
    /// tile type the project mentions, or when a used asset's animation frames draw from it.
    /// The editor's brush selection does not count. A coincidental match only hides a warning.
    let unused (project: GameProject) : CustomAsset list =
        let node =
            match JsonSerializer.SerializeToNode(project, JsonDefaults.Options) with
            | :? JsonObject as o -> o
            | _ -> JsonObject()
        node.Remove "customAssets" |> ignore
        node.Remove "selectedTileVisual" |> ignore
        let values = HashSet<string>(StringComparer.Ordinal)
        strings values node
        let refersTo (asset: CustomAsset) =
            values.Contains asset.Id
            || (asset.DataUrl.Length > 0 && values.Contains asset.DataUrl)
            || (asset.Type = CustomAssetTypes.Tile
                && (match asset.TileType with
                    | null -> false
                    | tileType -> values.Contains tileType))
        let used = HashSet<string>(StringComparer.Ordinal)
        let pending = Queue<CustomAsset>(project.CustomAssets |> Seq.filter refersTo)
        let byId = Dictionary<string, CustomAsset>(StringComparer.Ordinal)
        for asset in project.CustomAssets do
            byId.TryAdd(asset.Id, asset) |> ignore
        while pending.Count > 0 do
            let asset = pending.Dequeue()
            if used.Add asset.Id then
                match asset.Animations with
                | null -> ()
                | clips ->
                    for clip in clips do
                        for frame in clip.Frames do
                            match frame.AssetId with
                            | null -> ()
                            | id ->
                                match byId.TryGetValue id with
                                | true, other when not (used.Contains other.Id) -> pending.Enqueue other
                                | _ -> ()
        project.CustomAssets |> Seq.filter (fun asset -> not (used.Contains asset.Id)) |> List.ofSeq
