module FarmEngine.Export.Tests.AssetUsageTests

open System.Collections.Generic
open Xunit
open FarmEngine.Authoring
open FarmEngine.Export
open FarmEngine.Export.Tests.Support
open FarmEngine.Schemas

let private png = makePng 16 16 false

let private unusedIds (project: GameProject) = AssetUsage.unused project |> List.map (fun a -> a.Id)

[<Fact>]
let ``assets nothing refers to are unused`` () =
    let project = starter () |> withAssets [ asset "art-scratch" png ]
    Assert.Equal<string list>([ "art-scratch" ], unusedIds project)

[<Fact>]
let ``bindings, legacy images, frames, tile art and the export icon count as use`` () =
    let frameSource = asset "art-frames" png
    let animated =
        let clip = AnimationClip(Name = "walk", Frames = List<ArtFrame>([ ArtFrame(AssetId = "art-frames", Width = 16.0, Height = 16.0) ]))
        Records.withValue (asset "art-player" png) "Animations" (box (List<AnimationClip>([ clip ])))
    let legacy = asset "art-legacy" (makePng 16 16 true)
    let tile = Records.withValues (asset "art-grass" png) [ "Type", box CustomAssetTypes.Tile; "TileType", box TileTypes.Grass ]
    let icon = asset "art-icon" (makePng 256 256 false)
    let brushOnly = asset "art-brush" png
    let project =
        starter ()
        |> withAssets [ frameSource; animated; legacy; tile; icon; brushOnly ]
        |> fun p -> Records.withValues p [ "PlayerVisual", box (VisualRef(AssetId = "art-player")); "SelectedTileVisual", box (VisualRef(AssetId = "art-brush")) ]
        |> fun p ->
            let npc = Records.withValue p.Npcs[0] "CustomImage" (box legacy.DataUrl)
            Records.withValue p "Npcs" (box (List<Npc>(Seq.append [ npc ] (Seq.skip 1 p.Npcs))))
        |> withExport (fun s -> Records.withValue s "IconAssetId" (box "art-icon"))
    Assert.Equal<string list>([ "art-brush" ], unusedIds project)

[<Fact>]
let ``unused assets become export warnings and stay out of the cartridge`` () =
    let project = starter () |> withAssets [ asset "art-scratch" png ]
    let errors, warnings = Exporter.check project
    Assert.Empty errors
    Assert.Contains(warnings, fun w -> w.StartsWith("customAssets[0]: Asset \"art-scratch\"") && w.Contains("left out"))
    let clean = snd (Exporter.check (starter ()))
    Assert.DoesNotContain(clean, fun w -> w.Contains("customAssets"))
    // The compiled game carries no copy of the unused art.
    let cart = FarmEngine.Cart.Cartridge.GetRootAsCartridge(Google.FlatBuffers.ByteBuffer(CartridgeCompiler.Compile project))
    Assert.Equal(0, cart.AssetsLength)
    Assert.Empty(AssetUsage.used project)
