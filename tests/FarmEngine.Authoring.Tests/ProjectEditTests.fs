module FarmEngine.Authoring.Tests.ProjectEditTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

[<Fact>]
let ``project info and settings`` () =
    let project = starter ()
    let renamed = project |> apply (SetProjectInfo("  Sunny Acres ", "1.2.0"))
    Assert.Equal("Sunny Acres", renamed.Name)
    Assert.Equal("1.2.0", renamed.Version)
    Assert.Same(renamed, renamed |> apply (SetProjectInfo("Sunny Acres", "1.2.0")))
    let settings = { project.Settings with EnergyEnabled = false }
    let changed = project |> apply (SetSettings settings)
    Assert.False changed.Settings.EnergyEnabled
    Assert.Same(changed, changed |> apply (SetSettings settings))
    let graphics = { GraphicsSettings.Default with PixelArt = false }
    Assert.False((project |> apply (SetGraphics graphics)).Graphics.Value.PixelArt)

[<Fact>]
let ``export settings keep identity through rename, JSON and undo`` () =
    let project = starter ()
    let settings = Defaults.newExportSettings project
    Assert.StartsWith("local.", settings.GameId)
    let doc = Document.create project |> Document.apply (SetExportSettings(Some settings))
    let renamed = doc |> Document.apply (SetProjectInfo("Another Farm", "2.0.0"))
    Assert.Equal(settings.GameId, renamed.Project.Export.Value.GameId)
    let restored = roundTrip renamed.Project
    Assert.Equal(settings.GameId, restored.Export.Value.GameId)
    Assert.Equal(settings.ExecutableName, restored.Export.Value.ExecutableName)
    Assert.Equal(settings.GameId, (Document.undo renamed).Project.Export.Value.GameId)
    Assert.Same(project, (doc |> Document.undo).Project)
    Assert.DoesNotContain("\"export\"", ProjectLoad.toText project)

[<Fact>]
let ``removing a season drops its festivals and weather row but never the last season`` () =
    let project = starter ()
    let calendar = project.Settings.Calendar
    let festival = (Defaults.newFestival project calendar).Value
    let festival = { festival with SeasonId = "summer" }
    let withFestival = { calendar with Festivals = ([ festival ]) }
    let project = project |> apply (SetSettings({ project.Settings with Calendar = withFestival }))
    Assert.True(project.Weather.Table |> List.exists (fun (key, _) -> key = "summer"))
    let removed = project |> apply (RemoveSeason "summer")
    Assert.False(removed.Settings.Calendar.Seasons |> Seq.exists (fun s -> s.Id = "summer"))
    Assert.Empty removed.Settings.Calendar.Festivals
    Assert.False(removed.Weather.Table |> List.exists (fun (key, _) -> key = "summer"))
    let leftovers = errors removed |> List.filter (fun p -> p.Code.StartsWith "calendar.")
    Assert.True(leftovers.IsEmpty, describe leftovers)
    let one = removed |> apply (RemoveSeason "fall") |> apply (RemoveSeason "winter")
    Assert.Equal(1, one.Settings.Calendar.Seasons.Length)
    Assert.Same(one, one |> apply (RemoveSeason "spring"))

[<Fact>]
let ``assets bind and unbind everywhere`` () =
    let project = starter ()
    let asset = { CustomAsset.Default with Id = "art-cow"; Name = "cow.png"; Type = "art"; DataUrl = "data:,"; Width = Some(16.0); Height = Some(16.0) }
    let visual = { VisualRef.Default with AssetId = "art-cow" }
    let bindAll = Batch("art", [ UpsertAsset asset; BindVisual(NpcVisual "npc-farmer", Some visual); BindVisual(ItemVisual "material-wood", Some visual); BindVisual(PlayerVisual, Some visual) ])
    let bound = project |> apply bindAll
    Assert.Equal(Some visual, (npc bound "npc-farmer").Visual)
    Assert.Equal(Some visual, bound.PlayerVisual)
    Assert.Same(bound, bound |> apply (BindVisual(NpcVisual "npc-farmer", Some visual)))
    let art = errors bound |> List.filter (fun p -> p.Code.StartsWith "graphics.")
    Assert.True(art.IsEmpty, describe art)
    let unbound = bound |> apply (RemoveAsset "art-cow")
    Assert.True((npc unbound "npc-farmer").Visual.IsNone)
    Assert.True unbound.PlayerVisual.IsNone
    Assert.True((unbound.Items |> Seq.find (fun i -> i.Id = "material-wood")).Visual.IsNone)
    Assert.False(unbound.CustomAssets |> Seq.exists (fun a -> a.Id = "art-cow"))
    let artAfter = errors unbound |> List.filter (fun p -> p.Code.StartsWith "graphics.")
    Assert.True(artAfter.IsEmpty, describe artAfter)
    let unboundPlayer = bound |> apply (SetPlayerVisual None)
    Assert.True unboundPlayer.PlayerVisual.IsNone

[<Fact>]
let ``packs install once, toggle, reorder, remove and import`` () =
    let project = blank ()
    let pack = ProjectCatalog.CreateContentDefaultPack()
    let other = { pack with Manifest = { pack.Manifest with Id = "other-pack"; Name = "Other" } }
    let installed = project |> apply (Batch("install", [ InstallPack pack; InstallPack other ]))
    Assert.Equal(2, installed.ContentPacks.Length)
    Assert.Same(installed, installed |> apply (InstallPack pack))
    let disabled = installed |> apply (SetPackEnabled("other-pack", false))
    Assert.False(disabled.ContentPacks.[1].Enabled)
    Assert.Same(disabled, disabled |> apply (SetPackEnabled("other-pack", false)))
    let reordered = installed |> apply (ReorderPacks [ "other-pack" ])
    Assert.Equal("other-pack", reordered.ContentPacks.[0].Pack.Manifest.Id)
    Assert.Equal("content-default", reordered.ContentPacks.[1].Pack.Manifest.Id)
    Assert.Same(installed, installed |> apply (ReorderPacks [ "content-default"; "other-pack" ]))
    let removed = installed |> apply (RemovePack "other-pack")
    Assert.Equal(1, removed.ContentPacks.Length)
    let imported = removed |> apply (ImportPack "content-default")
    Assert.Empty imported.ContentPacks
    Assert.True(imported.Npcs |> Seq.exists (fun n -> n.Id = "npc-farmer"))
