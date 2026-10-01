module FarmEngine.Authoring.Tests.CartridgeTests

open System
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private read (bytes: byte[]) : CartContents =
    match CartridgeReader.read bytes with
    | Ok cart -> cart
    | Error message -> failwith message

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error message -> failwith message

let private length (json: Json) = match json with JArray items -> items.Length | _ -> -1

let private png = "data:image/png;base64,iVBORw0KGgo="

[<Fact>]
let ``format 2 carries start presentation and content without the project`` () =
    let cart = read (CartridgeCompiler.Compile(starter ()))
    Assert.Equal(2u, cart.CartFormat)
    Assert.Equal(9u, cart.ProjectSchemaVersion)
    Assert.True(CartridgeReader.hasIdentifier (CartridgeCompiler.Compile(starter ())))
    let start = parse cart.StartJson
    Assert.Equal(Some (starter ()).Id, Json.asString (Json.get "id" start))
    Assert.True(length (Json.get "scenes" start) > 0)
    Assert.Equal(Some (starter ()).Name, Json.asString (Json.get "name" (parse cart.PresentationJson)))
    Assert.True(length (Json.get "items" (parse cart.ContentJson)) > 0)
    Assert.Equal((starter ()).Name, cart.Info.Title)
    Assert.Equal((1280u, 800u, false), (cart.Info.WindowWidth, cart.Info.WindowHeight, cart.Info.Fullscreen))

[<Fact>]
let ``data urls move to the asset table once and are referenced by content hash`` () =
    let art = { CustomAsset.Default with Id = "art-cow"; Name = "cow.png"; Type = "art"; DataUrl = png; Width = Some(16.0); Height = Some(16.0) }
    let copy = { CustomAsset.Default with Id = "art-cow-2"; Name = "cow copy.png"; Type = "art"; DataUrl = png; Width = Some(16.0); Height = Some(16.0) }
    let project =
        starter ()
        |> apply (Batch("art", [ UpsertAsset art; UpsertAsset copy
                                 BindVisual(PlayerVisual, Some({ VisualRef.Default with AssetId = "art-cow" }))
                                 BindVisual(NpcVisual "npc-farmer", Some({ VisualRef.Default with AssetId = "art-cow-2" })) ]))
    let bytes = CartridgeCompiler.Compile project
    Assert.Equal<byte>(bytes, CartridgeCompiler.Compile project)
    let cart = read bytes
    Assert.Equal(1, cart.Assets.Length)
    let asset = cart.Assets.[0]
    Assert.Equal("image/png", asset.Mime)
    Assert.Equal<byte>(Convert.FromBase64String "iVBORw0KGgo=", asset.Data)
    let reference = Some("asset:" + asset.Id)
    let json = cart.PresentationJson
    Assert.DoesNotContain("data:", json)
    let assets = Json.get "customAssets" (parse json)
    Assert.Equal(reference, Json.asString (Json.get "dataUrl" (Json.index 0 assets)))
    Assert.Equal(reference, Json.asString (Json.get "dataUrl" (Json.index 1 assets)))
    Assert.DoesNotContain("data:image", cart.StartJson)

[<Fact>]
let ``malformed data urls stay inline`` () =
    let broken = { CustomAsset.Default with Id = "art-broken"; Name = "broken.png"; Type = "art"; DataUrl = "data:image/png;base64,***"; Width = Some(16.0); Height = Some(16.0) }
    let plain = { CustomAsset.Default with Id = "art-plain"; Name = "plain.svg"; Type = "art"; DataUrl = "data:image/svg+xml,%3Csvg%3E"; Width = Some(16.0); Height = Some(16.0) }
    let project =
        starter ()
        |> apply (Batch("art", [ UpsertAsset broken; UpsertAsset plain
                                 BindVisual(PlayerVisual, Some({ VisualRef.Default with AssetId = "art-broken" }))
                                 BindVisual(NpcVisual "npc-farmer", Some({ VisualRef.Default with AssetId = "art-plain" })) ]))
    let cart = read (CartridgeCompiler.Compile project)
    Assert.Empty cart.Assets
    let json = cart.PresentationJson
    Assert.Contains("data:image/png;base64,***", json)
    Assert.Contains("data:image/svg+xml,%3Csvg%3E", json)

[<Fact>]
let ``plugins of enabled packs ship with the hooks their manifest grants`` () =
    let pack (id: string) =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = id; Name = id; Version = "1.0.0"; Permissions = { PackPermissions.Default with Hooks = [ "onDayStart"; "onEffect" ] } }; Plugins = [ { PackPlugin.Default with Id = "greeter"; Hooks = [ "onEffect"; "onCommand"; "onDayStart" ]; Source = "api.on('onDayStart', () => [])" } ] }
    let project =
        { starter () with
            ContentPacks = [ { PackInstallation.Default with Pack = pack "alpha" }; { PackInstallation.Default with Pack = pack "off"; Enabled = false }; { PackInstallation.Default with Pack = pack "beta" } ] }
    let cart = read (CartridgeCompiler.Compile project)
    Assert.Equal(2, cart.Plugins.Length)
    let first = cart.Plugins.[0]
    Assert.Equal("alpha:greeter", first.Id)
    Assert.Equal("alpha", first.PackId)
    Assert.Equal("api.on('onDayStart', () => [])", first.Source)
    // The plugin's order, filtered by the manifest's permissions.
    Assert.Equal<string list>([ "onEffect"; "onDayStart" ], first.GrantedHooks)
    Assert.Equal("beta:greeter", cart.Plugins.[1].Id)
    Assert.Empty (read (CartridgeCompiler.Compile(starter ()))).Plugins

[<Fact>]
let ``unused art stays out of the cartridge`` () =
    let used = { CustomAsset.Default with Id = "art-used"; Name = "used.png"; Type = "art"; DataUrl = png; Width = Some(16.0); Height = Some(16.0) }
    let scratch = { CustomAsset.Default with Id = "art-scratch"; Name = "scratch.png"; Type = "art"; DataUrl = "data:image/png;base64,AAAA"; Width = Some(16.0); Height = Some(16.0) }
    let project = starter () |> apply (Batch("art", [ UpsertAsset used; UpsertAsset scratch; BindVisual(PlayerVisual, Some({ VisualRef.Default with AssetId = "art-used" })) ]))
    let cart = read (CartridgeCompiler.Compile project)
    Assert.Equal(1, cart.Assets.Length)
    let ids =
        match Json.get "customAssets" (parse cart.PresentationJson) with
        | JArray assets -> assets |> List.choose (Json.get "id" >> Json.asString)
        | _ -> []
    Assert.Equal<string list>([ "art-used" ], ids)

[<Fact>]
let ``the reader refuses what is not a cartridge`` () =
    Assert.Equal(Error "Not a cartridge (missing the FGCT identifier)", CartridgeReader.read [| 1uy; 2uy |])
    let bytes = CartridgeCompiler.Compile(starter ())
    match CartridgeReader.read (Array.sub bytes 0 64) with
    | Error message -> Assert.StartsWith("Malformed cartridge: ", message)
    | Ok _ -> failwith "a truncated cartridge read"

/// The sample-game cartridges farm-bench and the Rust tests load
/// (`fixtures/golden/cartridges/<sample>.cart`) are what the compiler makes of the samples today.
/// After an intended format or content change, rerun with FARM_RECORD_CARTRIDGES=1 to rewrite them.
[<Theory>]
[<InlineData("starter-farm", "starter"); InlineData("cozy-garden", "cozy"); InlineData("quest-rpg", "quest")>]
let ``the sample cartridges are current`` (file: string, sampleId: string) =
    let path = IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixtures", "golden", "cartridges", file + ".cart")
    let bytes = CartridgeCompiler.Compile(ProjectCatalog.CreateSampleProject(sampleId, 1.7e12))
    if Environment.GetEnvironmentVariable "FARM_RECORD_CARTRIDGES" = "1" then IO.File.WriteAllBytes(path, bytes)
    else
        Assert.True((IO.File.ReadAllBytes path = bytes), file + ".cart is stale; rerun with FARM_RECORD_CARTRIDGES=1")
        Assert.Equal(ProjectCatalog.CreateSampleProject(sampleId, 1.7e12).Name, (read bytes).Info.Title)

/// A game state as the engine writes it (only the parts Keep changes reads).
let private engineState (project: GameProject) (clock: (string * Json) list) (rest: (string * Json) list) : Json =
    let encoded = SchemaJson.encodeGameProject project
    JObject(
        [ "player", Json.get "player" encoded
          "world", JObject [ "scenes", Json.get "scenes" encoded ]
          "npcs", JObject []
          "quests", JObject []
          "clock", JObject([ "tick", JNumber 0.0; "timeMinutes", JNumber 360.0; "day", JNumber 1.0; "season", JString "spring"; "dayOfSeason", JNumber 1.0; "year", JNumber 1.0; "weatherId", JString "sun" ] |> List.map (fun (key, value) -> key, (clock |> List.tryFind (fst >> (=) key) |> Option.map snd |> Option.defaultValue value)))
          "flags", JObject []
          "animals", JArray []
          "social", JObject []
          "quarantinedItems", JArray []
          "mine", JObject [ "deepestFloor", JNumber 0.0; "currentFloor", JNumber 0.0 ]
          "dialogue", JNull
          "shop", JNull
          "minigame", JNull
          "shopPurchasesToday", JObject []
          "rng", JObject [ "algorithm", JString "xoshiro128ss"; "s", JArray [ JNumber 1.0; JNumber 2.0; JNumber 3.0; JNumber 4.0 ] ] ]
        |> List.map (fun (key, value) -> key, (rest |> List.tryFind (fst >> (=) key) |> Option.map snd |> Option.defaultValue value))
    )

[<Fact>]
let ``keep changes writes flag values and the live state back as they are`` () =
    let project = starter ()
    let flags = JObject [ "count", JNumber 3.0; "name", JString "Ada"; "met", JBool true ]
    let state =
        engineState project [ "tick", JNumber 1234.0 ] [
            "flags", flags
            "shop", JObject [ "shopId", JString "shop-general" ]
            "shopPurchasesToday", JObject [ "shop-general", JObject [ "seed-wheat", JNumber 4.0 ] ]
            "mine", JObject [ "deepestFloor", JNumber 3.0; "currentFloor", JNumber 2.0 ]
            "npcs", JObject [ "npc-farmer", JObject [ "x", JNumber 1.0; "y", JNumber 1.0; "sceneId", JString "scene-farm"; "patrolIndex", JNumber 1.0 ] ] ]
    match Playtest.applyState project state with
    | Error message -> failwith message
    | Ok kept ->
        Assert.Equal<(string * Json) list>([ "count", JNumber 3.0; "name", JString "Ada"; "met", JBool true ], kept.EventFlags)
        let keptState = kept.KeptState |> Option.defaultValue JNull
        Assert.Equal(JNumber 1234.0, Json.get "tick" keptState)
        Assert.Equal(JObject [ "shopId", JString "shop-general" ], Json.get "shop" keptState)
        Assert.Equal(JNumber 2.0, Json.get "mineCurrentFloor" keptState)
        Assert.Equal(JObject [ "npc-farmer", JObject [ "patrolIndex", JNumber 1.0 ] ], Json.get "npcs" keptState)
        Assert.True(Json.isNullish (Json.get "dialogue" keptState))
        // The next playtest's cartridge starts from it.
        let start = parse (read (CartridgeCompiler.CompileForPlaytest kept)).StartJson
        Assert.Equal(keptState, Json.get "keptState" start)
        Assert.Equal(JString "Ada", Json.get "name" (Json.get "eventFlags" start))
    // A fresh state has nothing extra to keep, and the start on day 1 of spring needs no day of season.
    match Playtest.applyState project (engineState project [] []) with
    | Ok kept ->
        Assert.Equal(None, kept.KeptState)
        Assert.Equal(None, kept.CurrentDayOfSeason)
    | Error message -> failwith message

[<Fact>]
let ``keep changes records the day of season when the absolute day does not give it`` () =
    let project = starter ()
    let state = engineState project [ "day", JNumber 11.0; "season", JString "summer"; "dayOfSeason", JNumber 11.0 ] []
    match Playtest.applyState project state with
    | Ok kept ->
        Assert.Equal(Some 11.0, kept.CurrentDayOfSeason)
        let start = parse (read (CartridgeCompiler.CompileForPlaytest kept)).StartJson
        Assert.Equal(JNumber 11.0, Json.get "currentDayOfSeason" start)
    | Error message -> failwith message

[<Fact>]
let ``keep changes keeps the project key order`` () =
    let project = starter ()
    let tool = { project with Player = { project.Player with EquippedTool = Some "tool-hoe" } }
    let keys (json: Json) = Json.keys json
    let before = SchemaJson.encodeGameProject tool
    // The playtest ends with no tool equipped: the key goes, the others stay in place.
    let after = Playtest.applyStateJson before (engineState project [] [])
    Assert.Equal<string list>(keys (Json.get "player" before) |> List.filter ((<>) "equippedTool"), keys (Json.get "player" after) |> List.filter (fun key -> Json.has key (Json.get "player" before)))
    Assert.Equal<string list>(keys before, keys after |> List.filter (fun key -> Json.has key before))
