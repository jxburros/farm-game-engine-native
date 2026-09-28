module FarmEngine.Authoring.Tests.ProjectCatalogTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Content
open FarmEngine.Json

let private same (expected: 'T) (actual: 'U) = Assert.Equal(StableJson.Stringify<'T> expected, StableJson.Stringify<'U> actual)

let templates : obj[] seq = seq {
    for id in ProjectTemplates.All do
        for now in [0.0; 123456789.0; 1790553600000.0] do yield [|box id; box now|]
}

[<Theory; MemberData(nameof templates)>]
let ``every F sharp template preserves the complete compatibility project`` (id: string) (now: float) =
    let expected = Templates.CreateProjectForTemplate(id, now)
    let actual = ProjectCatalog.CreateProjectForTemplate(id, now)
    same expected actual
    same (FarmEngine.Core.EngineState.CreateContentFromProject expected) (ProjectContent.Compile actual)
    Assert.Empty(Problems.collect actual |> Problems.errors)
    Assert.Equal<byte>(CartridgeCompiler.Compile expected, CartridgeCompiler.Compile actual)

[<Fact>]
let ``starter content pack player and template metadata match the web bridge`` () =
    same (DefaultContent.CreateContentDefaultPack()) (ProjectCatalog.CreateContentDefaultPack())
    same (DefaultContent.CreateDefaultPlayer("some-scene")) (ProjectCatalog.CreateDefaultPlayer("some-scene"))
    same Templates.TemplateInfo ProjectCatalog.TemplateInfo
    same ProjectTemplates.All ProjectCatalog.All
    same Templates.SampleIds ProjectCatalog.SampleIds

[<Theory>]
[<InlineData(null); InlineData(""); InlineData("starter"); InlineData("blank"); InlineData("cozy"); InlineData("quest"); InlineData("unknown")>]
let ``sample routing and unknown template fallbacks match`` (id: string) =
    same (Templates.CreateSampleProject(id, 42.0)) (ProjectCatalog.CreateSampleProject(id, 42.0))
    same (Templates.CreateProjectFromTemplate(id, 42.0)) (ProjectCatalog.CreateProjectFromTemplate(id, 42.0))
    same (Templates.CreateProjectForTemplate(id, 42.0)) (ProjectCatalog.CreateProjectForTemplate(id, 42.0))

[<Theory>]
[<InlineData(0.0); InlineData(35.0); InlineData(36.0); InlineData(-123456789.0); InlineData(1790553600000.0)>]
let ``project ids and named projects preserve compatibility with explicit time`` (now: float) =
    Assert.Equal(Templates.NewProjectId(now), ProjectCatalog.NewProjectId(now))
    for id in [null; "custom-id"; ""] do
        same (Templates.CreateNewProject("cozy", "My Garden", id, now))
             (ProjectCatalog.CreateNewProject("cozy", "My Garden", id, now))

[<Fact>]
let ``fresh projects do not share mutable defaults maps or catalog entries`` () =
    let original = ProjectCatalog.CreateInitialProject 0.0
    let baseline = StableJson.Stringify original
    let other = ProjectCatalog.CreateInitialProject 0.0
    other.Scenes[0].Tiles.Clear()
    other.Items.Clear()
    other.Settings.Calendar.Seasons.Clear()
    other.Weather.Types.Clear()
    Assert.Equal(baseline, StableJson.Stringify original)
    Assert.Equal(baseline, StableJson.Stringify(ProjectCatalog.CreateInitialProject 0.0))

[<Fact>]
let ``sample transformations leave the initial game independent`` () =
    let starter = ProjectCatalog.CreateInitialProject 0.0
    let cozy = ProjectCatalog.CreateCozyFarmProject 0.0
    let quest = ProjectCatalog.CreateQuestRpgProject 0.0
    Assert.True starter.Settings.EnergyEnabled
    Assert.False cozy.Settings.EnergyEnabled
    Assert.Equal(10.0, starter.Player.Inventory[0].Quantity)
    Assert.Equal(20.0, cozy.Player.Inventory[0].Quantity)
    Assert.Equal(2, starter.Npcs.Count)
    Assert.Equal(3, quest.Npcs.Count)
