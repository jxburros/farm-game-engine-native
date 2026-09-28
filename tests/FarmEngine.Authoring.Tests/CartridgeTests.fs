module FarmEngine.Authoring.Tests.CartridgeTests

open System
open System.Text
open System.Text.Json
open Google.FlatBuffers
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Cart
open FarmEngine.Schemas

let private read (bytes: byte[]) = Cartridge.GetRootAsCartridge(ByteBuffer(bytes))

let private text (cart: Cartridge) (section: Cartridge -> ArraySegment<byte> option) =
    match section cart with
    | Some segment -> Encoding.UTF8.GetString(segment.Array |> nonNull, segment.Offset, segment.Count)
    | None -> failwith "missing section"

let private start (cart: Cartridge) = text cart (fun c -> c.GetStartJsonBytes() |> Option.ofNullable)
let private presentation (cart: Cartridge) = text cart (fun c -> c.GetPresentationJsonBytes() |> Option.ofNullable)
let private content (cart: Cartridge) = text cart (fun c -> c.GetContentJsonBytes() |> Option.ofNullable)

let private png = "data:image/png;base64,iVBORw0KGgo="

[<Fact>]
let ``format 2 carries start presentation and content without the project`` () =
    let cart = read (CartridgeCompiler.Compile(starter ()))
    Assert.Equal(2u, cart.CartFormat)
    Assert.True(Cartridge.CartridgeBufferHasIdentifier(ByteBuffer(CartridgeCompiler.Compile(starter ()))))
    use startDoc = JsonDocument.Parse(start cart)
    Assert.Equal((starter ()).Id, startDoc.RootElement.GetProperty("id").GetString())
    Assert.True(startDoc.RootElement.GetProperty("scenes").GetArrayLength() > 0)
    use presentationDoc = JsonDocument.Parse(presentation cart)
    Assert.Equal((starter ()).Name, presentationDoc.RootElement.GetProperty("name").GetString())
    use contentDoc = JsonDocument.Parse(content cart)
    Assert.True(contentDoc.RootElement.GetProperty("items").GetArrayLength() > 0)

[<Fact>]
let ``data urls move to the asset table once and are referenced by content hash`` () =
    let art = CustomAsset(Id = "art-cow", Name = "cow.png", Type = "art", DataUrl = png, Width = 16.0, Height = 16.0)
    let copy = CustomAsset(Id = "art-cow-2", Name = "cow copy.png", Type = "art", DataUrl = png, Width = 16.0, Height = 16.0)
    let project =
        starter ()
        |> apply (Batch("art", [ UpsertAsset art; UpsertAsset copy; BindVisual(PlayerVisual, Some(VisualRef(AssetId = "art-cow"))) ]))
    let bytes = CartridgeCompiler.Compile project
    Assert.Equal<byte>(bytes, CartridgeCompiler.Compile project)
    let cart = read bytes
    Assert.Equal(1, cart.AssetsLength)
    let asset = cart.Assets(0).Value
    Assert.Equal("image/png", asset.Mime)
    Assert.Equal<byte>(Convert.FromBase64String "iVBORw0KGgo=", asset.GetDataArray())
    let reference = "asset:" + asset.Id
    let json = presentation cart
    Assert.DoesNotContain("data:", json)
    use doc = JsonDocument.Parse json
    let assets = doc.RootElement.GetProperty("customAssets")
    Assert.Equal(reference, assets[0].GetProperty("dataUrl").GetString())
    Assert.Equal(reference, assets[1].GetProperty("dataUrl").GetString())
    Assert.DoesNotContain("data:image", start cart)

[<Fact>]
let ``malformed data urls stay inline`` () =
    let broken = CustomAsset(Id = "art-broken", Name = "broken.png", Type = "art", DataUrl = "data:image/png;base64,***", Width = 16.0, Height = 16.0)
    let plain = CustomAsset(Id = "art-plain", Name = "plain.svg", Type = "art", DataUrl = "data:image/svg+xml,%3Csvg%3E", Width = 16.0, Height = 16.0)
    let project = starter () |> apply (Batch("art", [ UpsertAsset broken; UpsertAsset plain ]))
    let cart = read (CartridgeCompiler.Compile project)
    Assert.Equal(0, cart.AssetsLength)
    let json = presentation cart
    Assert.Contains("data:image/png;base64,***", json)
    Assert.Contains("data:image/svg+xml,%3Csvg%3E", json)

[<Fact>]
let ``plugins of enabled packs ship with the hooks their manifest grants`` () =
    let pack (id: string) =
        ContentPack(
            Manifest = PackManifest(Id = id, Name = id, Version = "1.0.0", Permissions = PackPermissions(Hooks = listOf [ "onDayStart"; "onEffect" ])),
            Plugins = listOf [ PackPlugin(Id = "greeter", Hooks = listOf [ "onEffect"; "onCommand"; "onDayStart" ], Source = "api.on('onDayStart', () => [])") ])
    let project =
        Records.withValue (starter ()) "ContentPacks"
            (box (listOf [ PackInstallation(Pack = pack "alpha"); PackInstallation(Pack = pack "off", Enabled = false); PackInstallation(Pack = pack "beta") ]))
    let cart = read (CartridgeCompiler.Compile project)
    Assert.Equal(2, cart.PluginsLength)
    let first = cart.Plugins(0).Value
    Assert.Equal("alpha:greeter", first.Id)
    Assert.Equal("alpha", first.PackId)
    Assert.Equal("api.on('onDayStart', () => [])", first.Source)
    // The plugin's order, filtered by the manifest's permissions.
    Assert.Equal<string>([| "onEffect"; "onDayStart" |], [| for i in 0 .. first.GrantedHooksLength - 1 -> first.GrantedHooks(i) |])
    Assert.Equal("beta:greeter", cart.Plugins(1).Value.Id)
    Assert.Equal(0, (read (CartridgeCompiler.Compile(starter ()))).PluginsLength)
