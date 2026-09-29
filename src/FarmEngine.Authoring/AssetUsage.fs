namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Which custom assets the game uses (docs/EXPORT.md "Only used assets ship"). The cartridge
/// compiler leaves the others out, and Export Game lists them as warnings.
module AssetUsage =
    /// Every string value in a JSON tree.
    let rec private strings (found: HashSet<string>) (json: Json) =
        match json with
        | JObject members ->
            for _, value in members do
                strings found value
        | JArray items ->
            for value in items do
                strings found value
        | JString s -> found.Add s |> ignore
        | _ -> ()

    /// Assets nothing in the game refers to. An asset counts as used when any value in the
    /// project outside the asset list names its id or its data URL (visual bindings, legacy
    /// custom images, crop art, pack content, the export icon), when it is the tile art for a
    /// tile type the project mentions, or when a used asset's animation frames draw from it.
    /// The editor's brush selection does not count. A coincidental match only keeps an asset
    /// (and hides its warning); a used asset is never left out.
    let unused (project: GameProject) : CustomAsset list =
        let json =
            SchemaJson.encodeGameProject project
            |> Json.remove "customAssets"
            |> Json.remove "selectedTileVisual"
        let values = HashSet<string>()
        strings values json
        let refersTo (asset: CustomAsset) =
            values.Contains asset.Id
            || (asset.DataUrl.Length > 0 && values.Contains asset.DataUrl)
            || (asset.Type = CustomAssetTypes.Tile
                && (match asset.TileType with
                    | None -> false
                    | Some tileType -> values.Contains tileType))
        let used = HashSet<string>()
        let pending = Queue<CustomAsset>(project.CustomAssets |> List.filter refersTo)
        let byId = Dictionary<string, CustomAsset>()
        for asset in project.CustomAssets do
            if not (byId.ContainsKey asset.Id) then byId.[asset.Id] <- asset
        while pending.Count > 0 do
            let asset = pending.Dequeue()
            if used.Add asset.Id then
                for clip in defaultArg asset.Animations [] do
                    for frame in clip.Frames do
                        match frame.AssetId with
                        | None -> ()
                        | Some id ->
                            match byId.TryGetValue id with
                            | true, other when not (used.Contains other.Id) -> pending.Enqueue other
                            | _ -> ()
        project.CustomAssets |> List.filter (fun asset -> not (used.Contains asset.Id))

    /// The assets the game uses, in project order.
    let used (project: GameProject) : CustomAsset list =
        let unusedIds = HashSet<string>(unused project |> List.map (fun asset -> asset.Id))
        project.CustomAssets |> List.filter (fun asset -> not (unusedIds.Contains asset.Id))
