module FarmEngine.Authoring.Tests.PackMergeTests

open System.IO
open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Content
open FarmEngine.Core
open FarmEngine.Json
open FarmEngine.Schemas

let private compare (baseContent: GameContent) (installs: System.Collections.Generic.List<PackInstallation>) =
    let expected = Packs.MergePacksIntoContent(baseContent, installs)
    let actualContent, actualProblems = PackMerge.mergeIntoContent baseContent installs
    Assert.Equal(StableJson.Stringify expected.Content, StableJson.Stringify actualContent)
    Assert.Equal<(string * string * string) list>(
        expected.Problems |> Seq.map (fun p -> p.PackId, p.Severity, p.Message) |> Seq.toList,
        actualProblems |> List.map (fun p -> p.PackId, p.Severity, p.Message))

let private compareImport (project: GameProject) (pack: ContentPack) =
    let expected = Packs.ApplyPackToProject(project, pack)
    let actualProject, actualProblems = PackMerge.applyToProject project pack
    Assert.Equal(StableJson.Stringify expected.Project, StableJson.Stringify actualProject)
    Assert.Equal<(string * string * string) list>(
        expected.Problems |> Seq.map (fun p -> p.PackId, p.Severity, p.Message) |> Seq.toList,
        actualProblems |> List.map (fun p -> p.PackId, p.Severity, p.Message))

[<Fact>]
let ``F sharp pack merge matches the compatibility engine on installed fixture packs`` () =
    let path = Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "packs-project.json")
    let project = JsonSerializer.Deserialize<GameProject>(File.ReadAllText path, JsonDefaults.Options)
    let baseContent = EngineState.CreateBaseContentFromProject project
    compare baseContent project.ContentPacks

[<Fact>]
let ``F sharp pack order and merge match overrides missing dependencies and cycles`` () =
    let baseContent = EngineState.CreateBaseContentFromProject(blank ())
    let alpha =
        ContentPack(
            Manifest = PackManifest(Id = "alpha", Name = "Alpha", Version = "1.0.0"),
            Content = PackContent(Items = listOf [ item "ore" "Alpha Ore" ]))
    let beta =
        ContentPack(
            Manifest = PackManifest(Id = "beta", Name = "Beta", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "alpha") ],
                Overrides = listOf [ "alpha:ore" ]),
            Content = PackContent(Items = listOf [ item "alpha:ore" "Better Ore" ]))
    let gamma =
        ContentPack(
            Manifest = PackManifest(Id = "gamma", Name = "Gamma", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "missing") ]),
            Content = PackContent(Items = listOf [ item "alpha:ore" "Undeclared Ore" ]))
    let delta =
        ContentPack(
            Manifest = PackManifest(Id = "delta", Name = "Delta", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "epsilon") ]))
    let epsilon =
        ContentPack(
            Manifest = PackManifest(Id = "epsilon", Name = "Epsilon", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "delta") ]))
    let installs =
        listOf [ PackInstallation(Pack = gamma)
                 PackInstallation(Pack = beta)
                 PackInstallation(Pack = alpha)
                 PackInstallation(Pack = delta)
                 PackInstallation(Pack = epsilon) ]
    compare baseContent installs
    let merged, problems = PackMerge.mergeIntoContent baseContent installs
    Assert.Equal("Better Ore", merged.Items |> Seq.find (fun i -> i.Id = "alpha:ore") |> fun i -> i.Name)
    Assert.Contains(problems, fun p -> p.Message.Contains "not installed/enabled")
    Assert.Contains(problems, fun p -> p.Message.Contains "Dependency cycle")
    Assert.Contains(problems, fun p -> p.Message.Contains "without declaring")

[<Fact>]
let ``disabled packs leave content intact and report no problems`` () =
    let baseContent = EngineState.CreateBaseContentFromProject(blank ())
    let disabled =
        listOf [ PackInstallation(
                    Enabled = false,
                    Pack = ContentPack(Manifest = PackManifest(Id = "disabled", Name = "Disabled", Version = "1.0.0"),
                        Content = PackContent(Items = listOf [ item "ore" "Ore" ]))) ]
    let merged, problems = PackMerge.mergeIntoContent baseContent disabled
    Assert.Same(baseContent, merged)
    Assert.Empty problems

[<Fact>]
let ``F sharp pack import matches the compatibility engine for starter and installed packs`` () =
    compareImport (blank ()) (DefaultContent.CreateContentDefaultPack())
    let path = Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "packs-project.json")
    let project = JsonSerializer.Deserialize<GameProject>(File.ReadAllText path, JsonDefaults.Options)
    for installation in project.ContentPacks do
        compareImport project installation.Pack
