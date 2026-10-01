namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open FarmEngine.Schemas

/// The deterministic F# cartridge compiler (format 2, `schemas/cart.fbs`). A project is split
/// into the compiled content, the inputs of a new game (`start`, Rust `StartState`) and what
/// presentation reads (`presentation`, Rust `Presentation`); the player never reads project
/// JSON. The sections stay compatibility JSON until the native-numerics cutover. Every base64
/// `data:` URL inside them moves to the asset table and is replaced by `asset:<id>`, where the
/// id is a content hash, so equal files are stored once and ids never depend on order. Plain F#
/// throughout (`FlatBufferBuilder`, `Bytes`), so the web version compiles cartridges with the
/// same code.
[<AbstractClass; Sealed>]
type CartridgeCompiler =
    /// The cartridge format this compiler writes (Rust `farm_cart::CART_FORMAT`).
    static member Format = 2u

    /// The inputs of `create_game_state` (Rust `StartState::from_project`).
    static member StartSection(project: GameProject) : Json =
        let optional (name: string) (value: 'T option) (encode: 'T -> Json) = value |> Option.map (fun v -> name, encode v) |> Option.toList
        let quests =
            project.Quests
            |> List.map (fun quest ->
                JObject
                    [ "id", JString quest.Id
                      "status", JString quest.Status
                      "objectives",
                      JArray(
                          quest.Objectives
                          |> List.map (fun objective ->
                              JObject [ "id", JString objective.Id; "progress", JNumber objective.Progress; "completed", JBool objective.Completed ])
                      ) ])
        let npcs =
            project.Npcs
            |> List.map (fun npc ->
                JObject [ "id", JString npc.Id; "x", JNumber npc.X; "y", JNumber npc.Y; "sceneId", JString npc.SceneId ])
        // meta.packs of a new game: the enabled packs in load order (Rust `stamp_packs`).
        let packs =
            project.ContentPacks
            |> List.filter (fun install -> install.Enabled)
            |> List.map (fun install -> JObject [ "id", JString install.Pack.Manifest.Id; "version", JString install.Pack.Manifest.Version ])
        JObject
            [ yield "id", JString project.Id
              yield "gameStartTime", JNumber project.GameStartTime
              yield "settings", SchemaJson.encodeProjectSettings project.Settings
              yield "player", SchemaJson.encodePlayer project.Player
              yield "quests", JArray quests
              yield "npcs", JArray npcs
              yield "eventFlags", Encode.dict id project.EventFlags
              yield "currentTimeMinutes", JNumber project.CurrentTimeMinutes
              yield "currentDay", JNumber project.CurrentDay
              yield "currentSeason", JString project.CurrentSeason
              yield! optional "currentDayOfSeason" project.CurrentDayOfSeason JNumber
              yield "currentYear", JNumber project.CurrentYear
              yield! optional "currentWeatherId" project.CurrentWeatherId JString
              yield "scenes", Encode.list SchemaJson.encodeScene project.Scenes
              yield "packs", JArray packs
              yield! optional "socialState" project.SocialState (Encode.dict SchemaJson.encodeNpcSocialState)
              yield "animals", Encode.list SchemaJson.encodeAnimalState project.Animals
              yield! optional "mineDeepestFloor" project.MineDeepestFloor JNumber
              yield! optional "quarantinedItems" project.QuarantinedItems (Encode.list SchemaJson.encodeInventorySlot)
              yield! optional "rngState" project.RngState SchemaJson.encodeRngState
              yield! optional "keptState" project.KeptState id ]

    /// What the renderer and the game panels read (Rust `Presentation::from_project`).
    static member PresentationSection(project: GameProject) : Json =
        let optional (name: string) (value: 'T option) (encode: 'T -> Json) = value |> Option.map (fun v -> name, encode v) |> Option.toList
        JObject
            [ yield "name", JString project.Name
              // Only the art the game uses ships (docs/EXPORT.md); the editor keeps the rest.
              yield "customAssets", Encode.list SchemaJson.encodeCustomAsset (AssetUsage.used project)
              yield! optional "customCrops" project.CustomCrops (Encode.list SchemaJson.encodeCustomCropDefinition)
              yield! optional "playerCustomImage" project.PlayerCustomImage JString
              yield! optional "playerVisual" project.PlayerVisual SchemaJson.encodeVisualRef
              yield! optional "graphics" project.Graphics SchemaJson.encodeGraphicsSettings
              yield "gamePanels", Encode.list SchemaJson.encodeGamePanel (defaultArg project.GamePanels [])
              yield "showMadeWithCredit", JBool project.Settings.ShowMadeWithCredit ]

    /// Moves every base64 `data:` URL string inside `json` into `assets` (id → mime, bytes) and
    /// replaces it with `asset:<id>`. Other strings (and malformed data URLs) stay as they are.
    static member ExtractAssets(json: Json, assets: Dictionary<string, string * byte[]>) : Json =
        let rewrite (text: string) : string option =
            if not (text.Length >= 5 && text.Substring(0, 5).ToLowerInvariant() = "data:") then None
            else
                let comma = text.IndexOf ','
                if comma < 0 then None
                else
                    let header = text.Substring(5, comma - 5)
                    if not (header.ToLowerInvariant().EndsWith(";base64", StringComparison.Ordinal)) then None
                    else
                        let mime = header.Substring(0, header.Length - 7)
                        let mime = if String.IsNullOrEmpty mime then "application/octet-stream" else mime
                        match Bytes.fromBase64 (JsNumber.trim (text.Substring(comma + 1))) with
                        | None -> None
                        | Some bytes ->
                            let hash = Bytes.sha256 (Array.append (Bytes.utf8 (mime + "\n")) bytes)
                            let id = Bytes.toHex (Array.sub hash 0 16)
                            assets.[id] <- (mime, bytes)
                            Some("asset:" + id)
        let rec walk (json: Json) : Json =
            match json with
            | JObject members -> JObject(members |> List.map (fun (key, value) -> key, walk value))
            | JArray items -> JArray(List.map walk items)
            | JString text ->
                match rewrite text with
                | Some reference -> JString reference
                | None -> json
            | other -> other
        walk json

    /// The cartridge of a project whose Problems report no errors (Export Game, farmc); throws
    /// `InvalidOperationException` listing the errors otherwise.
    static member Compile(project: GameProject) : byte[] =
        let problems = Problems.collect project |> Problems.errors
        if not problems.IsEmpty then
            invalidOp (problems |> List.map (fun p -> p.Path + ": " + p.Message) |> String.concat "\n")
        CartridgeCompiler.Build project

    /// The cartridge the editor's Play Mode runs: the same bytes as `Compile`, without refusing
    /// a project that still has errors (a playtest of unfinished work is allowed, as on the web).
    static member CompileForPlaytest(project: GameProject) : byte[] = CartridgeCompiler.Build project

    /// A window side inside the range Export accepts (`ChecksExport`), so a playtest of a
    /// project with an out-of-range size still gets a usable window.
    static member WindowSide(value: int, low: int) : uint32 = uint32 (max low (min 8192 value))

    /// A number as an unsigned field: clamped and truncated first, because .NET saturates a
    /// float conversion where JavaScript's `>>> 0` wraps, and the bytes must be the same in both.
    static member UInt32Of(value: float) : uint32 =
        if Double.IsNaN value || value <= 0.0 then 0u
        elif value >= 4294967295.0 then UInt32.MaxValue
        else uint32 (Math.Truncate value)

    static member private Build(project: GameProject) : byte[] =
        let settings = defaultArg project.Export (Defaults.newExportSettings project)
        let title = defaultArg settings.Title project.Name
        let version = defaultArg settings.Version project.Version
        let executable = defaultArg settings.ExecutableName (Defaults.slugId title Seq.empty "game")

        // JSON sections, with their embedded files moved to the asset table.
        let assets = Dictionary<string, string * byte[]>()
        let section (json: Json) = Bytes.utf8 (Json.stringify (CartridgeCompiler.ExtractAssets(json, assets)))
        let contentBytes = section (SchemaJson.encodeGameContent (ContentCompiler.compile project))
        let startBytes = section (CartridgeCompiler.StartSection project)
        let presentationBytes = section (CartridgeCompiler.PresentationSection project)

        let builder = FlatBufferBuilder(4096)
        let optionalString (value: string option) =
            match value with
            | None -> 0
            | Some text -> builder.CreateString text

        // FlatBuffers writes back-to-front. Create all referenced values before each table; the
        // fields go in the order flatc's `Create…` helpers add them.
        let assetOffsets =
            // In id order (lowercase hex, so `compare` is ordinal).
            [| for id in assets.Keys |> Seq.sortWith compare ->
                   let mime, bytes = assets.[id]
                   let idOffset = builder.CreateString id
                   let mimeOffset = builder.CreateString mime
                   let dataOffset = builder.CreateByteVector bytes
                   builder.StartTable 3
                   builder.AddOffsetField(2, dataOffset)
                   builder.AddOffsetField(1, mimeOffset)
                   builder.AddOffsetField(0, idOffset)
                   builder.EndTable() |]
        let assetsOffset = builder.CreateOffsetVector assetOffsets
        // Plugins of the enabled packs in load order, each with the hooks its manifest grants
        // (Rust `farm_plugins::plugin_specs_from_project`).
        let pluginOffsets =
            [| for install in project.ContentPacks do
                   if install.Enabled then
                       let pack = install.Pack
                       let granted = HashSet<string>(pack.Manifest.Permissions.Hooks)
                       for plugin in pack.Plugins do
                           let hooks = plugin.Hooks |> List.filter granted.Contains |> List.map builder.CreateString |> Array.ofList
                           let hooksOffset = builder.CreateOffsetVector hooks
                           let idOffset = builder.CreateString(pack.Manifest.Id + ":" + plugin.Id)
                           let packOffset = builder.CreateString pack.Manifest.Id
                           let sourceOffset = builder.CreateString plugin.Source
                           builder.StartTable 4
                           builder.AddOffsetField(3, hooksOffset)
                           builder.AddOffsetField(2, sourceOffset)
                           builder.AddOffsetField(1, packOffset)
                           builder.AddOffsetField(0, idOffset)
                           yield builder.EndTable() |]
        let pluginsOffset = builder.CreateOffsetVector pluginOffsets
        let titleOffset = builder.CreateString title
        let versionOffset = builder.CreateString version
        let gameIdOffset = builder.CreateString settings.GameId
        let authorOffset = optionalString settings.Author
        let companyOffset = optionalString settings.Company
        let executableOffset = builder.CreateString executable
        let scaleOffset = builder.CreateString settings.PixelScale
        let creditsOffset = optionalString settings.Credits
        builder.StartTable 11
        builder.AddOffsetField(10, creditsOffset)
        builder.AddOffsetField(9, scaleOffset)
        builder.AddUInt32Field(7, CartridgeCompiler.WindowSide(settings.Window.Height, 240), 800u)
        builder.AddUInt32Field(6, CartridgeCompiler.WindowSide(settings.Window.Width, 320), 1280u)
        builder.AddOffsetField(5, executableOffset)
        builder.AddOffsetField(4, companyOffset)
        builder.AddOffsetField(3, authorOffset)
        builder.AddOffsetField(2, gameIdOffset)
        builder.AddOffsetField(1, versionOffset)
        builder.AddOffsetField(0, titleOffset)
        builder.AddBoolField(8, settings.Window.Fullscreen, false)
        let info = builder.EndTable()
        let contentOffset = builder.CreateByteVector contentBytes
        let startOffset = builder.CreateByteVector startBytes
        let presentationOffset = builder.CreateByteVector presentationBytes
        builder.StartTable 9
        builder.AddOffsetField(8, pluginsOffset)
        builder.AddOffsetField(7, assetsOffset)
        builder.AddOffsetField(6, presentationOffset)
        builder.AddOffsetField(5, startOffset)
        builder.AddOffsetField(4, contentOffset)
        builder.AddOffsetField(2, info)
        builder.AddUInt32Field(1, CartridgeCompiler.UInt32Of project.SchemaVersion, 0u)
        builder.AddUInt32Field(0, CartridgeCompiler.Format, 0u)
        let cart = builder.EndTable()
        builder.Finish(cart, "FGCT")
        builder.ToArray()
