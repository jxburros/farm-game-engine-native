module FarmEngine.Authoring.Tests.ContentCompilerTests

open System
open System.IO
open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Core
open FarmEngine.Json
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private same (expected: 'T) (actual: 'U) = Assert.Equal(StableJson.Stringify<'T> expected, StableJson.Stringify<'U> actual)

[<Fact>]
let ``authoring has no reference to the C sharp simulation assembly`` () =
    Assert.DoesNotContain(typeof<Document>.Assembly.GetReferencedAssemblies(), fun a -> a.Name = "FarmEngine.Core")

[<Fact>]
let ``all built in content definitions match the compatibility catalog`` () =
    same ContentBuiltin.CropDefinitions (Builtin.crops ())
    same (ContentBuiltin.CreateDefaultItems()) (Builtin.items ())
    same ContentBuiltin.DefaultNodeTypes (Builtin.nodeTypes ())
    same ContentBuiltin.MineNodeTypes (Builtin.mineNodeTypes ())
    same (ContentBuiltin.CreateDefaultMineBands()) (Builtin.mineBands ())
    same (ContentBuiltin.CreateDefaultRecipes()) (Builtin.recipes ())
    same (ContentBuiltin.CreateDefaultMachineTypes()) (Builtin.machineTypes ())
    same (ContentBuiltin.CreateDefaultAnimalSpecies()) (Builtin.animalSpecies ())
    same (ContentBuiltin.CreateDefaultFishTables()) (Builtin.fishTables ())
    same (ContentBuiltin.CreateDefaultShop()) (Builtin.shop ())

[<Fact>]
let ``built in factories do not share mutable collections between projects`` () =
    let crops = Builtin.crops ()
    crops["wheat"].Seasons.Clear()
    Assert.NotEmpty((Builtin.crops ()).["wheat"].Seasons)
    let nodes = Builtin.nodeTypes ()
    nodes[0].Drops.Clear()
    Assert.NotEmpty((Builtin.nodeTypes ()).[0].Drops)

let contentGoldens : obj[] seq =
    Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "content"), "*.json")
    |> Seq.map (fun path -> [| box path |])

[<Theory; MemberData(nameof contentGoldens)>]
let ``F sharp content matches TypeScript stable JSON and hash`` (path: string) =
    use fixture = JsonDocument.Parse(File.ReadAllText path)
    let root = fixture.RootElement
    let project = root.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)
    let before = StableJson.Stringify project
    let content = ContentCompiler.compile project
    Assert.Equal(root.GetProperty("stable").GetString(), StableJson.Stringify content)
    Assert.Equal(root.GetProperty("contentHash").GetString(), Hash.HashState content)
    Assert.Equal(before, StableJson.Stringify project)

let replayProjects : obj[] seq =
    Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "replays"), "*.json")
    |> Seq.filter (fun path -> Path.GetFileName path <> "index.json")
    |> Seq.map (fun path -> [| box path |])

[<Theory; MemberData(nameof replayProjects)>]
let ``F sharp content matches the compatibility compiler for every replay project`` (path: string) =
    use fixture = JsonDocument.Parse(File.ReadAllText path)
    let project = fixture.RootElement.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)
    same (EngineState.CreateBaseContentFromProject project) (ContentCompiler.baseContent project)
    same (EngineState.CreateContentFromProject project) (ContentCompiler.compile project)

[<Fact>]
let ``compiler handles all templates and fallback settings`` () =
    for _, project in ("blank", blank ()) :: ("starter", starter ()) :: templates () do
        same (EngineState.CreateContentFromProject project) (ContentCompiler.compile project)
    let project = blank ()
    let variants =
        [ Records.withValue project "Items" (box (System.Collections.Generic.List<Item>()))
          Records.withValue project "Settings" null
          Records.withValue project "Settings" (box (ProjectSettings(MaxEnergy = -1.0)))
          Records.withValue project "Weather" null
          Records.withValue project "Weather" (box (WeatherConfig(Types = listOf [WeatherTypeDefinition(CropDamageChance = 2.0)])))
          Records.withValue project "NodeTypes" (box (listOf [NodeTypeDefinition(Id = "node-tree", Name = "Custom tree")]))
          Records.withValue project "CustomCrops" (box (listOf [CustomCropDefinition(Id = "wheat", Name = "Custom wheat", CustomAsset = "art")])) ]
    for variant in variants do
        same (EngineState.CreateBaseContentFromProject variant) (ContentCompiler.baseContent variant)

[<Theory>]
[<InlineData("grass"); InlineData("soil"); InlineData("path"); InlineData("wall"); InlineData("door"); InlineData("water")>]
let ``authored tile construction and layer edits match the web bridge`` tileType =
    let tile = Tiles.CreateEmptyTile(3.0, 4.0, tileType)
    same tile (AuthoringTiles.CreateEmptyTile(3.0, 4.0, tileType))
    for next in ["grass"; "path"; "wall"; "door"] do
        for visual in [null; VisualRef(AssetId = "art")] do
            let initial = Records.withValues tile ["CustomImage", box "legacy.png"; "Visuals", box (TileVisuals(Overlay = VisualRef(AssetId = "old")))]
            let before = StableJson.Stringify initial
            same (Tiles.SetTileLayer(initial, next, visual)) (AuthoringTiles.SetTileLayer(initial, next, visual))
            Assert.Equal(before, StableJson.Stringify initial)
    same (Tiles.CreateEmptyScene("s", "Scene", 3.0, 4.0)) (AuthoringTiles.CreateEmptyScene("s", "Scene", 3.0, 4.0))
