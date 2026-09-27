module FarmEngine.Authoring.Tests.ProjectEditTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Content
open FarmEngine.Schemas

[<Fact>]
let ``project info and settings`` () =
    let project = starter ()
    let renamed = project |> apply (SetProjectInfo("  Sunny Acres ", "1.2.0"))
    Assert.Equal("Sunny Acres", renamed.Name)
    Assert.Equal("1.2.0", renamed.Version)
    Assert.Same(renamed, renamed |> apply (SetProjectInfo("Sunny Acres", "1.2.0")))
    let settings = Records.withValue project.Settings "EnergyEnabled" (box false)
    let changed = project |> apply (SetSettings settings)
    Assert.False changed.Settings.EnergyEnabled
    Assert.Same(changed, changed |> apply (SetSettings settings))
    let graphics = GraphicsSettings(PixelArt = false)
    Assert.False((nonNull (project |> apply (SetGraphics graphics)).Graphics).PixelArt)

[<Fact>]
let ``removing a season drops its festivals and weather row but never the last season`` () =
    let project = starter ()
    let calendar = project.Settings.Calendar
    let festival = nonNull (Defaults.newFestival project calendar |> Option.toObj)
    let festival = Records.withValue festival "SeasonId" (box "summer")
    let withFestival = Records.withValue calendar "Festivals" (box (listOf [ festival ]))
    let project = project |> apply (SetSettings(Records.withValue project.Settings "Calendar" (box withFestival)))
    Assert.True(project.Weather.Table.ContainsKey "summer")
    let removed = project |> apply (RemoveSeason "summer")
    Assert.False(removed.Settings.Calendar.Seasons |> Seq.exists (fun s -> s.Id = "summer"))
    Assert.Empty removed.Settings.Calendar.Festivals
    Assert.False(removed.Weather.Table.ContainsKey "summer")
    let leftovers = errors removed |> List.filter (fun p -> p.Code.StartsWith "calendar.")
    Assert.True(leftovers.IsEmpty, describe leftovers)
    let one = removed |> apply (RemoveSeason "fall") |> apply (RemoveSeason "winter")
    Assert.Equal(1, one.Settings.Calendar.Seasons.Count)
    Assert.Same(one, one |> apply (RemoveSeason "spring"))

[<Fact>]
let ``assets bind and unbind everywhere`` () =
    let project = starter ()
    let asset = CustomAsset(Id = "art-cow", Name = "cow.png", Type = "art", DataUrl = "data:,", Width = 16.0, Height = 16.0)
    let visual = VisualRef(AssetId = "art-cow")
    let bindAll = Batch("art", [ UpsertAsset asset; BindVisual(NpcVisual "npc-farmer", Some visual); BindVisual(ItemVisual "material-wood", Some visual); BindVisual(PlayerVisual, Some visual) ])
    let bound = project |> apply bindAll
    Assert.Same(visual, (npc bound "npc-farmer").Visual)
    Assert.Same(visual, bound.PlayerVisual)
    Assert.Same(bound, bound |> apply (BindVisual(NpcVisual "npc-farmer", Some visual)))
    let art = errors bound |> List.filter (fun p -> p.Code.StartsWith "graphics.")
    Assert.True(art.IsEmpty, describe art)
    let unbound = bound |> apply (RemoveAsset "art-cow")
    Assert.Null((npc unbound "npc-farmer").Visual)
    Assert.Null unbound.PlayerVisual
    Assert.Null((unbound.Items |> Seq.find (fun i -> i.Id = "material-wood")).Visual)
    Assert.False(unbound.CustomAssets |> Seq.exists (fun a -> a.Id = "art-cow"))
    let artAfter = errors unbound |> List.filter (fun p -> p.Code.StartsWith "graphics.")
    Assert.True(artAfter.IsEmpty, describe artAfter)
    let unboundPlayer = bound |> apply (SetPlayerVisual None)
    Assert.Null unboundPlayer.PlayerVisual

[<Fact>]
let ``packs install once, toggle, reorder, remove and import`` () =
    let project = blank ()
    let pack = DefaultContent.CreateContentDefaultPack()
    let other = Records.withValue pack "Manifest" (box (Records.withValues pack.Manifest [ ("Id", box "other-pack"); ("Name", box "Other") ]))
    let installed = project |> apply (Batch("install", [ InstallPack pack; InstallPack other ]))
    Assert.Equal(2, installed.ContentPacks.Count)
    Assert.Same(installed, installed |> apply (InstallPack pack))
    let disabled = installed |> apply (SetPackEnabled("other-pack", false))
    Assert.False(disabled.ContentPacks.[1].Enabled)
    Assert.Same(disabled, disabled |> apply (SetPackEnabled("other-pack", false)))
    let reordered = installed |> apply (ReorderPacks [ "other-pack" ])
    Assert.Equal("other-pack", reordered.ContentPacks.[0].Pack.Manifest.Id)
    Assert.Equal("content-default", reordered.ContentPacks.[1].Pack.Manifest.Id)
    Assert.Same(installed, installed |> apply (ReorderPacks [ "content-default"; "other-pack" ]))
    let removed = installed |> apply (RemovePack "other-pack")
    Assert.Equal(1, removed.ContentPacks.Count)
    let imported = removed |> apply (ImportPack "content-default")
    Assert.Empty imported.ContentPacks
    Assert.True(imported.Npcs |> Seq.exists (fun n -> n.Id = "npc-farmer"))
