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
        let clip = { AnimationClip.Default with Name = "walk"; Frames = [ { ArtFrame.Default with AssetId = Some "art-frames"; Width = 16.0; Height = 16.0 } ] }
        { asset "art-player" png with Animations = Some [ clip ] }
    let legacy = asset "art-legacy" (makePng 16 16 true)
    let tile = { asset "art-grass" png with Type = CustomAssetTypes.Tile; TileType = Some TileTypes.Grass }
    let icon = asset "art-icon" (makePng 256 256 false)
    let brushOnly = asset "art-brush" png
    let project =
        starter ()
        |> withAssets [ frameSource; animated; legacy; tile; icon; brushOnly ]
        |> fun p ->
            { p with
                PlayerVisual = Some { VisualRef.Default with AssetId = "art-player" }
                SelectedTileVisual = Some { VisualRef.Default with AssetId = "art-brush" } }
        |> fun p -> { p with Npcs = { p.Npcs[0] with CustomImage = Some legacy.DataUrl } :: List.tail p.Npcs }
        |> withExport (fun s -> { s with IconAssetId = Some "art-icon" })
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
    match CartridgeReader.read (CartridgeCompiler.Compile project) with
    | Ok cart -> Assert.Empty cart.Assets
    | Error message -> failwith message
    Assert.Empty(AssetUsage.used project)
