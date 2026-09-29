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
    Assert.Equal(8u, cart.ProjectSchemaVersion)
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
