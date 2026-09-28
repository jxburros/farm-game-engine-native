module FarmEngine.Authoring.Tests.ContentCompilerTests

open System
open System.IO
open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Json
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private same (expected: 'T) (actual: 'U) = Assert.Equal(StableJson.Stringify<'T> expected, StableJson.Stringify<'U> actual)

[<Fact>]
let ``authoring has no reference to the C sharp engine assemblies`` () =
    let referenced = typeof<Document>.Assembly.GetReferencedAssemblies() |> Array.map (fun a -> a.Name)
    for engine in [ "FarmEngine.Core"; "FarmEngine.Content"; "FarmEngine.Runtime"; "FarmEngine.Rendering" ] do
        Assert.DoesNotContain(engine, referenced)

/// `fixtures/projects/content-builtin.json`: content-builtin.ts's catalogs, recorded from the
/// TypeScript engine (the Rust `packs_and_state` tests check the same file).
[<Fact>]
let ``all built in content definitions match TypeScript`` () =
    let expected = readElement [ "Fixtures"; "content-builtin.json" ]
    let check (key: string) (actual: 'T) =
        assertSameStable key (StableJson.Stringify(expected.GetProperty key: JsonElement)) (StableJson.Stringify<'T> actual)
    check "crops" (Builtin.crops ())
    check "items" (Builtin.items ())
    check "nodes" (Builtin.nodeTypes ())
    check "mineNodes" (Builtin.mineNodeTypes ())
    check "bands" (Builtin.mineBands ())
    check "recipes" (Builtin.recipes ())
    check "machineTypes" (Builtin.machineTypes ())
    check "animals" (Builtin.animalSpecies ())
    check "fish" (Builtin.fishTables ())
    check "shop" (Builtin.shop ())

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
    Assert.Equal(root.GetProperty("contentHash").GetString(), hashState content)
    Assert.Equal(before, StableJson.Stringify project)

let replayProjects : obj[] seq =
    Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "replays"), "*.json")
    |> Seq.filter (fun path -> Path.GetFileName path <> "index.json")
    |> Seq.map (fun path -> [| box path |])

/// Every replay records the hash of the content the TypeScript engine assembled for its project.
[<Theory; MemberData(nameof replayProjects)>]
let ``F sharp content matches the TypeScript content hash of every replay project`` (path: string) =
    use fixture = JsonDocument.Parse(File.ReadAllText path)
    let project = fixture.RootElement.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)
    Assert.Equal(fixture.RootElement.GetProperty("contentHash").GetString(), hashState (ContentCompiler.compile project))

[<Fact>]
let ``compiler falls back to the defaults for missing or unloadable settings`` () =
    let project = blank ()
    let baseOf (name: string) (value: objnull) = ContentCompiler.baseContent (Records.withValue project name value)
    // No items: the built-in catalog.
    same (Builtin.items ()) (baseOf "Items" (box (System.Collections.Generic.List<Item>()))).Items
    // Settings that would not load: the defaults.
    same SettingsSchema.DefaultProjectSettings (baseOf "Settings" null).Settings
    same SettingsSchema.DefaultProjectSettings (baseOf "Settings" (box (ProjectSettings(MaxEnergy = -1.0)))).Settings
    // Weather that would not load: the default weather.
    same (MigrationsSchema.DefaultWeatherConfig()) (baseOf "Weather" null).Weather
    same (MigrationsSchema.DefaultWeatherConfig())
        (baseOf "Weather" (box (WeatherConfig(Types = listOf [ WeatherTypeDefinition(CropDamageChance = 2.0) ])))).Weather
    // Valid project settings are kept as authored.
    let settings = Records.withValue project.Settings "MaxEnergy" (box 42.0)
    Assert.Equal(42.0, (baseOf "Settings" (box settings)).Settings.MaxEnergy)

[<Fact>]
let ``project node types and custom crops replace the built in ones`` () =
    let project = blank ()
    let nodes =
        (ContentCompiler.baseContent (Records.withValue project "NodeTypes" (box (listOf [ NodeTypeDefinition(Id = "node-tree", Name = "Custom tree") ])))).NodeTypes
    let builtIn = Seq.append (Builtin.nodeTypes ()) (Builtin.mineNodeTypes ()) |> Seq.map (fun d -> d.Id) |> List.ofSeq
    // Built-in and mine node types the project does not replace, then the project's own.
    Assert.Equal<string list>((builtIn |> List.filter ((<>) "node-tree")) @ [ "node-tree" ], nodes |> Seq.map (fun d -> d.Id) |> List.ofSeq)
    Assert.Equal("Custom tree", (nodes |> Seq.last).Name)
    let crops =
        (ContentCompiler.baseContent
            (Records.withValue project "CustomCrops" (box (listOf [ CustomCropDefinition(Id = "wheat", Name = "Custom wheat", CustomAsset = "art") ])))).Crops
    Assert.Equal<string list>((Builtin.crops ()).Keys |> List.ofSeq, crops.Keys |> List.ofSeq)
    Assert.Equal("Custom wheat", crops.["wheat"].Name)
    // The custom crop's own fields (customAsset included) carry over.
    Assert.Contains("\"customAsset\":\"art\"", StableJson.Stringify crops.["wheat"])

// ── Authored tiles (game-helpers.ts, characterized by the C# GameHelpersCharacterizationTests) ──

let private assertJson (expected: string) (actual: 'T) =
    Assert.Equal(StableJson.Stringify(JsonDocument.Parse(expected).RootElement: JsonElement), StableJson.Stringify<'T> actual)

[<Theory>]
[<InlineData("grass", "background"); InlineData("soil", "background"); InlineData("water", "background"); InlineData("floor", "background")>]
[<InlineData("path", "overlay"); InlineData("wall", "object"); InlineData("door", "object")>]
let ``tile types classify onto their layer`` (tileType: string, layer: string) =
    Assert.Equal(layer, AuthoringTiles.ClassifyTileType tileType)

[<Fact>]
let ``empty tiles route each type to its layer and only walls collide`` () =
    assertJson
        """{"x":3,"y":7,"type":"grass","background":"grass","overlay":null,"object":null,"collision":false,"soilMoisture":0,"soilFertility":0}"""
        (AuthoringTiles.CreateEmptyTile(3.0, 7.0, "grass"))
    for tileType in [ "soil"; "water"; "floor" ] do
        let tile = AuthoringTiles.CreateEmptyTile(0.0, 0.0, tileType)
        Assert.Equal((tileType, tileType), (tile.Type, tile.Background))
        Assert.Null tile.Overlay
        Assert.Null tile.Object
        Assert.False tile.Collision
    let path = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "path")
    Assert.Equal(("path", "grass", "path", false), (path.Type, path.Background, path.Overlay, path.Collision))
    Assert.Null path.Object
    let wall = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "wall")
    Assert.Equal(("grass", "wall", true), (wall.Background, wall.Object, wall.Collision))
    Assert.Null wall.Overlay
    // Quirk: doors are walkable.
    let door = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "door")
    Assert.Equal(("grass", "door", false), (door.Background, door.Object, door.Collision))

[<Fact>]
let ``set tile layer paints one layer and returns a new tile`` () =
    let grass = AuthoringTiles.CreateEmptyTile(0.0, 0.0, "grass")
    let before = StableJson.Stringify grass
    let soil = AuthoringTiles.SetTileLayer(grass, "soil")
    Assert.NotSame(grass, soil)
    Assert.Equal(before, StableJson.Stringify grass)
    // Quirk: painting a background under a wall keeps the wall object and its collision.
    let underWall = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "wall"), "soil")
    Assert.Equal(("soil", "soil", "wall", true), (underWall.Type, underWall.Background, underWall.Object, underWall.Collision))
    let path = AuthoringTiles.SetTileLayer(grass, "path")
    Assert.Equal(("path", "grass", "path", false), (path.Type, path.Background, path.Overlay, path.Collision))
    Assert.Null path.Object
    let wall = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "soil"), "wall")
    Assert.Equal(("wall", "soil", "wall", true), (wall.Type, wall.Background, wall.Object, wall.Collision))
    let door = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "wall"), "door")
    Assert.Equal(("door", "door", false), (door.Type, door.Object, door.Collision))

[<Fact>]
let ``set tile layer with a visual binds it to the painted layer`` () =
    let tile = Records.withValues (AuthoringTiles.CreateEmptyTile(3.0, 4.0, "grass")) [ "CustomImage", box "legacy.png"; "Visuals", box (TileVisuals(Overlay = VisualRef(AssetId = "old"))) ]
    let before = StableJson.Stringify tile
    let painted = AuthoringTiles.SetTileLayer(tile, "wall", VisualRef(AssetId = "art"))
    Assert.Null painted.CustomImage
    Assert.Equal("art", painted.Visuals.Object.AssetId)
    Assert.Equal("old", painted.Visuals.Overlay.AssetId)
    // Without a visual the legacy image stays and the layer's binding clears.
    let plain = AuthoringTiles.SetTileLayer(tile, "path")
    Assert.Equal("legacy.png", plain.CustomImage)
    Assert.Null plain.Visuals.Overlay
    Assert.Equal(before, StableJson.Stringify tile)

[<Fact>]
let ``empty scenes are rows of grass tiles with nothing placed`` () =
    let scene = AuthoringTiles.CreateEmptyScene("scene-x", "X", 4.0, 3.0)
    Assert.Equal(("scene-x", "X", 4.0, 3.0), (scene.Id, scene.Name, scene.Width, scene.Height))
    Assert.Equal(3, scene.Tiles.Count)
    for y in 0 .. 2 do
        Assert.Equal(4, scene.Tiles.[y].Count)
        for x in 0 .. 3 do
            same (AuthoringTiles.CreateEmptyTile(float x, float y, "grass")) scene.Tiles.[y].[x]
    Assert.Empty scene.Transitions
    Assert.Empty scene.Npcs
    Assert.Empty scene.Events
