namespace FarmEngine.Authoring

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FarmEngine.Cart
open FarmEngine.Json
open FarmEngine.Schemas
open Google.FlatBuffers

/// The deterministic F# cartridge compiler (format 2, `schemas/cart.fbs`). A project is split
/// into the compiled content, the inputs of a new game (`start`, Rust `StartState`) and what
/// presentation reads (`presentation`, Rust `Presentation`); the player never reads project
/// JSON. The sections stay compatibility JSON until the native-numerics cutover. Every base64
/// `data:` URL inside them moves to the asset table and is replaced by `asset:<id>`, where the
/// id is a content hash, so equal files are stored once and ids never depend on order.
[<AbstractClass; Sealed>]
type CartridgeCompiler =
    /// The cartridge format this compiler writes (Rust `farm_cart::CART_FORMAT`).
    static member Format = 2u

    /// The inputs of `create_game_state` (Rust `StartState::from_project`).
    static member StartSection(project: GameProject) : JsonObject =
        let node (value: obj) = JsonSerializer.SerializeToNode(value, value.GetType(), JsonDefaults.Options)
        let start = JsonObject()
        let add (name: string) (value: JsonNode | null) = start[name] <- value
        let addOptional (name: string) (value: obj | null) =
            match value with
            | null -> ()
            | value -> add name (node value)
        add "id" (JsonValue.Create project.Id)
        add "gameStartTime" (JsonValue.Create project.GameStartTime)
        add "settings" (node project.Settings)
        add "player" (node project.Player)
        let quests = JsonArray()
        for quest in project.Quests do
            let objectives = JsonArray()
            for objective in quest.Objectives do
                let entry = JsonObject()
                entry["id"] <- JsonValue.Create objective.Id
                entry["progress"] <- JsonValue.Create objective.Progress
                entry["completed"] <- JsonValue.Create objective.Completed
                objectives.Add entry
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create quest.Id
            entry["status"] <- JsonValue.Create quest.Status
            entry["objectives"] <- objectives
            quests.Add entry
        add "quests" quests
        let npcs = JsonArray()
        for npc in project.Npcs do
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create npc.Id
            entry["x"] <- JsonValue.Create npc.X
            entry["y"] <- JsonValue.Create npc.Y
            entry["sceneId"] <- JsonValue.Create npc.SceneId
            npcs.Add entry
        add "npcs" npcs
        add "eventFlags" (node project.EventFlags)
        add "currentTimeMinutes" (JsonValue.Create project.CurrentTimeMinutes)
        add "currentDay" (JsonValue.Create project.CurrentDay)
        add "currentSeason" (JsonValue.Create project.CurrentSeason)
        add "currentYear" (JsonValue.Create project.CurrentYear)
        addOptional "currentWeatherId" project.CurrentWeatherId
        add "scenes" (node project.Scenes)
        // meta.packs of a new game: the enabled packs in load order (Rust `stamp_packs`).
        let packs = JsonArray()
        for install in project.ContentPacks do
            if install.Enabled then
                let entry = JsonObject()
                entry["id"] <- JsonValue.Create install.Pack.Manifest.Id
                entry["version"] <- JsonValue.Create install.Pack.Manifest.Version
                packs.Add entry
        add "packs" packs
        addOptional "socialState" project.SocialState
        add "animals" (node project.Animals)
        addOptional "mineDeepestFloor" (Option.toObj (Option.ofNullable project.MineDeepestFloor |> Option.map box))
        addOptional "quarantinedItems" project.QuarantinedItems
        addOptional "rngState" project.RngState
        start

    /// What the renderer and the game panels read (Rust `Presentation::from_project`).
    static member PresentationSection(project: GameProject) : JsonObject =
        let node (value: obj) = JsonSerializer.SerializeToNode(value, value.GetType(), JsonDefaults.Options)
        let presentation = JsonObject()
        let addOptional (name: string) (value: obj | null) =
            match value with
            | null -> ()
            | value -> presentation[name] <- node value
        presentation["name"] <- JsonValue.Create project.Name
        // Only the art the game uses ships (docs/EXPORT.md); the editor keeps the rest.
        presentation["customAssets"] <- node (Collections.Generic.List<CustomAsset>(AssetUsage.used project))
        addOptional "customCrops" project.CustomCrops
        addOptional "playerCustomImage" project.PlayerCustomImage
        addOptional "playerVisual" project.PlayerVisual
        addOptional "graphics" project.Graphics
        presentation["gamePanels"] <-
            match project.GamePanels with
            | null -> JsonArray() :> JsonNode | null
            | panels -> node panels
        presentation["showMadeWithCredit"] <- JsonValue.Create project.Settings.ShowMadeWithCredit
        presentation

    /// Moves every base64 `data:` URL string inside `node` into `assets` (id → mime, bytes) and
    /// replaces it with `asset:<id>`. Other strings (and malformed data URLs) stay as they are.
    static member ExtractAssets(root: JsonNode, assets: Collections.Generic.SortedDictionary<string, string * byte[]>) : unit =
        let rewrite (text: string) : string option =
            if not (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) then None
            else
                let comma = text.IndexOf ','
                if comma < 0 then None
                else
                    let header = text.Substring(5, comma - 5)
                    if not (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) then None
                    else
                        let mime = header.Substring(0, header.Length - 7)
                        let mime = if String.IsNullOrEmpty mime then "application/octet-stream" else mime
                        match (try Some(Convert.FromBase64String(text.Substring(comma + 1).Trim())) with :? FormatException -> None) with
                        | None -> None
                        | Some bytes ->
                            let hash = SHA256.HashData(Array.append (Encoding.UTF8.GetBytes(mime + "\n")) bytes)
                            let id = Convert.ToHexString(hash, 0, 16).ToLowerInvariant()
                            assets[id] <- (mime, bytes)
                            Some("asset:" + id)
        let rec walk (node: JsonNode | null) : JsonNode | null =
            match node with
            | :? JsonObject as obj ->
                for key in obj |> Seq.map (fun pair -> pair.Key) |> Array.ofSeq do
                    let child = obj[key]
                    let replaced = walk child
                    if not (Object.ReferenceEquals(child, replaced)) then obj[key] <- replaced
                obj
            | :? JsonArray as array ->
                for i in 0 .. array.Count - 1 do
                    let child = array[i]
                    let replaced = walk child
                    if not (Object.ReferenceEquals(child, replaced)) then array[i] <- replaced
                array
            | :? JsonValue as value ->
                match value.GetValueKind() with
                | JsonValueKind.String ->
                    match rewrite (value.GetValue<string>()) with
                    | Some reference -> JsonValue.Create reference
                    | None -> value
                | _ -> value
            | other -> other
        walk root |> ignore

    static member Compile(project: GameProject) : byte[] =
        let problems = Problems.collect project |> Problems.errors
        if not problems.IsEmpty then
            raise (InvalidOperationException(problems |> List.map (fun p -> p.Path + ": " + p.Message) |> String.concat "\n"))

        let settings =
            match project.Export with
            | null -> Defaults.newExportSettings project
            | export -> export
        let title = match settings.Title with null -> project.Name | value -> value
        let version = match settings.Version with null -> project.Version | value -> value
        let executable = match settings.ExecutableName with null -> Defaults.slugId title Seq.empty "game" | value -> value

        // JSON sections, with their embedded files moved to the asset table.
        let assets = Collections.Generic.SortedDictionary<string, string * byte[]>(StringComparer.Ordinal)
        let section (node: JsonNode) =
            CartridgeCompiler.ExtractAssets(node, assets)
            Encoding.UTF8.GetBytes(node.ToJsonString(JsonDefaults.Options))
        let content = ContentCompiler.compile project
        let contentNode =
            match JsonSerializer.SerializeToNode<GameContent>(content, JsonDefaults.Options) with
            | null -> invalidOp "Compiled content serialized to null."
            | node -> node
        let contentBytes = section contentNode
        let startBytes = section (CartridgeCompiler.StartSection project)
        let presentationBytes = section (CartridgeCompiler.PresentationSection project)

        let builder = FlatBufferBuilder(4096)
        let stringOffset (value: string) = builder.CreateString value
        let optionalStringOffset (value: string | null) =
            match value with
            | null -> StringOffset(0)
            | text -> stringOffset text

        // FlatBuffers writes back-to-front. Create all referenced values before each table.
        let assetOffsets =
            [| for KeyValue(id, (mime, bytes)) in assets ->
                   let idOffset = stringOffset id
                   let mimeOffset = stringOffset mime
                   let dataOffset = Asset.CreateDataVector(builder, bytes)
                   Asset.CreateAsset(builder, idOffset, mimeOffset, dataOffset) |]
        let assetsOffset = Cartridge.CreateAssetsVector(builder, assetOffsets)
        // Plugins of the enabled packs in load order, each with the hooks its manifest grants
        // (the C# `Plugins.PluginSpecsFromProject`, Rust `farm_plugins::plugin_specs_from_project`).
        let pluginOffsets =
            [| for install in project.ContentPacks do
                   if install.Enabled then
                       let pack = install.Pack
                       // JSON nulls can reach these lists despite their annotations (as `?? []` in C#).
                       let orEmpty (items: seq<'T>) = if isNull (box items) then Seq.empty else items
                       let granted = Collections.Generic.HashSet<string>(orEmpty pack.Manifest.Permissions.Hooks, StringComparer.Ordinal)
                       for plugin in orEmpty pack.Plugins do
                               let hooks = orEmpty plugin.Hooks |> Seq.filter granted.Contains |> Seq.map stringOffset |> Array.ofSeq
                               let hooksOffset = Plugin.CreateGrantedHooksVector(builder, hooks)
                               let idOffset = stringOffset (pack.Manifest.Id + ":" + plugin.Id)
                               let packOffset = stringOffset pack.Manifest.Id
                               let sourceOffset = stringOffset plugin.Source
                               yield Plugin.CreatePlugin(builder, idOffset, packOffset, sourceOffset, hooksOffset) |]
        let pluginsOffset = Cartridge.CreatePluginsVector(builder, pluginOffsets)
        let titleOffset = stringOffset title
        let versionOffset = stringOffset version
        let gameIdOffset = stringOffset settings.GameId
        let authorOffset = optionalStringOffset settings.Author
        let companyOffset = optionalStringOffset settings.Company
        let executableOffset = stringOffset executable
        let scaleOffset = stringOffset settings.PixelScale
        let creditsOffset = optionalStringOffset settings.Credits
        let info =
            GameInfo.CreateGameInfo(builder, titleOffset, versionOffset, gameIdOffset,
                                    authorOffset, companyOffset, executableOffset,
                                    uint32 settings.Window.Width, uint32 settings.Window.Height,
                                    settings.Window.Fullscreen, scaleOffset, creditsOffset)
        let contentOffset = Cartridge.CreateContentJsonVector(builder, contentBytes)
        let startOffset = Cartridge.CreateStartJsonVector(builder, startBytes)
        let presentationOffset = Cartridge.CreatePresentationJsonVector(builder, presentationBytes)
        let cart =
            Cartridge.CreateCartridge(builder, CartridgeCompiler.Format, uint32 project.SchemaVersion, info,
                                      contentOffset, startOffset, presentationOffset, assetsOffset, pluginsOffset)
        Cartridge.FinishCartridgeBuffer(builder, cart)
        builder.SizedByteArray()
