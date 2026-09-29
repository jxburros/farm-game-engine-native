module FarmEngine.Authoring.Tests.ContentCompilerTests

open System
open System.IO
open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private same (encode: 'T -> Json) (expected: 'T) (actual: 'T) = Assert.Equal(stableOf encode expected, stableOf encode actual)

[<Fact>]
let ``authoring has no reference to the C sharp engine assemblies`` () =
    let referenced = typeof<Document>.Assembly.GetReferencedAssemblies() |> Array.map (fun a -> a.Name)
    for engine in [ "FarmEngine.Core"; "FarmEngine.Content"; "FarmEngine.Runtime"; "FarmEngine.Rendering" ] do
        Assert.DoesNotContain(engine, referenced)

/// `fixtures/projects/content-builtin.json`: content-builtin.ts's catalogs, recorded from the
/// TypeScript engine (the Rust `packs_and_state` tests check the same file).
[<Fact>]
let ``all built in content definitions match TypeScript`` () =
    let expected = readJson [ "Fixtures"; "content-builtin.json" ]
    let check (key: string) (actual: Json) =
        assertSameStable key (Json.stableStringify (Json.get key expected)) (Json.stableStringify actual)
    check "crops" (Encode.dict SchemaJson.encodeCropDefinition (Builtin.crops ()))
    check "items" (Encode.list SchemaJson.encodeItem (Builtin.items ()))
    check "nodes" (Encode.list SchemaJson.encodeNodeTypeDefinition (Builtin.nodeTypes ()))
    check "mineNodes" (Encode.list SchemaJson.encodeNodeTypeDefinition (Builtin.mineNodeTypes ()))
    check "bands" (Encode.list SchemaJson.encodeMineBand (Builtin.mineBands ()))
    check "recipes" (Encode.list SchemaJson.encodeRecipeDefinition (Builtin.recipes ()))
    check "machineTypes" (Encode.list SchemaJson.encodeMachineTypeDefinition (Builtin.machineTypes ()))
    check "animals" (Encode.list SchemaJson.encodeAnimalSpeciesDefinition (Builtin.animalSpecies ()))
    check "fish" (Encode.list SchemaJson.encodeFishTable (Builtin.fishTables ()))
    check "shop" (SchemaJson.encodeShopDefinition (Builtin.shop ()))

let contentGoldens : obj[] seq =
    Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "content"), "*.json")
    |> Seq.map (fun path -> [| box path |])

[<Theory; MemberData(nameof contentGoldens)>]
let ``F sharp content matches TypeScript stable JSON and hash`` (path: string) =
    let root = readJson [ path ]
    let project = projectOf (Json.get "project" root)
    let before = stableOf SchemaJson.encodeGameProject project
    let content = SchemaJson.encodeGameContent (ContentCompiler.compile project)
    Assert.Equal(Json.asString (Json.get "stable" root), Some(Json.stableStringify content))
    Assert.Equal(Json.asString (Json.get "contentHash" root), Some(hashJson content))
    Assert.Equal(before, stableOf SchemaJson.encodeGameProject project)

let replayProjects : obj[] seq =
    Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "replays"), "*.json")
    |> Seq.filter (fun path -> Path.GetFileName path <> "index.json")
    |> Seq.map (fun path -> [| box path |])

/// Every replay records the hash of the content the TypeScript engine assembled for its project.
[<Theory; MemberData(nameof replayProjects)>]
let ``F sharp content matches the TypeScript content hash of every replay project`` (path: string) =
    let root = readJson [ path ]
    let project = projectOf (Json.get "project" root)
    Assert.Equal(Json.asString (Json.get "contentHash" root), Some(hashJson (SchemaJson.encodeGameContent (ContentCompiler.compile project))))

[<Fact>]
let ``compiler falls back to the defaults for missing or unloadable settings`` () =
    let project = blank ()
    // No items: the built-in catalog.
    Assert.Equal<Item list>(Builtin.items (), (ContentCompiler.baseContent { project with Items = [] }).Items)
    // Settings that would not load: the defaults.
    let settingsOf (settings: ProjectSettings) = (ContentCompiler.baseContent { project with Settings = settings }).Settings
    same SchemaJson.encodeProjectSettings SettingsSchema.DefaultProjectSettings (settingsOf { ProjectSettings.Default with MaxEnergy = -1.0 })
    // Weather that would not load: the default weather.
    let broken = { WeatherConfig.Default with Types = [ { WeatherTypeDefinition.Default with CropDamageChance = 2.0 } ] }
    same SchemaJson.encodeWeatherConfig (MigrationsSchema.DefaultWeatherConfig()) (ContentCompiler.baseContent { project with Weather = broken }).Weather
    // Valid project settings are kept as authored.
    Assert.Equal(42.0, (settingsOf { project.Settings with MaxEnergy = 42.0 }).MaxEnergy)

[<Fact>]
let ``project node types and custom crops replace the built in ones`` () =
    let project = blank ()
    let nodes =
        (ContentCompiler.baseContent ({ project with NodeTypes = ([ { NodeTypeDefinition.Default with Id = "node-tree"; Name = "Custom tree" } ]) })).NodeTypes
    let builtIn = Builtin.nodeTypes () @ Builtin.mineNodeTypes () |> List.map (fun d -> d.Id)
    // Built-in and mine node types the project does not replace, then the project's own.
    Assert.Equal<string list>((builtIn |> List.filter ((<>) "node-tree")) @ [ "node-tree" ], nodes |> Seq.map (fun d -> d.Id) |> List.ofSeq)
    Assert.Equal("Custom tree", (nodes |> Seq.last).Name)
    let crops =
        (ContentCompiler.baseContent
            ({ project with CustomCrops = Some [ { CustomCropDefinition.Default with Id = "wheat"; Name = "Custom wheat"; CustomAsset = Some("art") } ] })).Crops
    Assert.Equal<string list>(Builtin.crops () |> List.map fst, crops |> List.map fst)
    let wheat = crops |> List.find (fst >> (=) "wheat") |> snd
    Assert.Equal("Custom wheat", wheat.Name)
    // The custom crop's own fields (customAsset included) carry over.
    Assert.Contains("\"customAsset\":\"art\"", stableOf SchemaJson.encodeCropDefinition wheat)

// ── Authored tiles (game-helpers.ts, characterized by the C# GameHelpersCharacterizationTests) ──

let private assertTile (expected: string) (actual: Tile) =
    match Json.parse expected with
    | Ok json -> Assert.Equal(Json.stableStringify json, stableOf SchemaJson.encodeTile actual)
    | Error message -> failwith message

[<Theory>]
[<InlineData("grass", "background"); InlineData("soil", "background"); InlineData("water", "background"); InlineData("floor", "background")>]
[<InlineData("path", "overlay"); InlineData("wall", "object"); InlineData("door", "object")>]
let ``tile types classify onto their layer`` (tileType: string, layer: string) =
    Assert.Equal(layer, AuthoringTiles.ClassifyTileType tileType)

[<Fact>]
let ``empty tiles route each type to its layer and only walls collide`` () =
    assertTile
        """{"x":3,"y":7,"type":"grass","background":"grass","overlay":null,"object":null,"collision":false,"soilMoisture":0,"soilFertility":0}"""
        (AuthoringTiles.CreateEmptyTile(3.0, 7.0, "grass"))
    for tileType in [ "soil"; "water"; "floor" ] do
        let tile = AuthoringTiles.CreateEmptyTile(0.0, 0.0, tileType)
        Assert.Equal((tileType, tileType), (tile.Type, tile.Background))
        Assert.Equal(None, tile.Overlay)
        Assert.Equal(None, tile.Object)
        Assert.False tile.Collision
    let path = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "path")
    Assert.Equal(("path", "grass", Some "path", false), (path.Type, path.Background, path.Overlay, path.Collision))
    Assert.Equal(None, path.Object)
    let wall = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "wall")
    Assert.Equal(("grass", Some "wall", true), (wall.Background, wall.Object, wall.Collision))
    Assert.Equal(None, wall.Overlay)
    // Quirk: doors are walkable.
    let door = AuthoringTiles.CreateEmptyTile(1.0, 2.0, "door")
    Assert.Equal(("grass", Some "door", false), (door.Background, door.Object, door.Collision))

[<Fact>]
let ``set tile layer paints one layer and returns a new tile`` () =
    let grass = AuthoringTiles.CreateEmptyTile(0.0, 0.0, "grass")
    let before = stableOf SchemaJson.encodeTile grass
    let soil = AuthoringTiles.SetTileLayer(grass, "soil")
    Assert.NotSame(grass, soil)
    Assert.Equal(before, stableOf SchemaJson.encodeTile grass)
    // Quirk: painting a background under a wall keeps the wall object and its collision.
    let underWall = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "wall"), "soil")
    Assert.Equal(("soil", "soil", Some "wall", true), (underWall.Type, underWall.Background, underWall.Object, underWall.Collision))
    let path = AuthoringTiles.SetTileLayer(grass, "path")
    Assert.Equal(("path", "grass", Some "path", false), (path.Type, path.Background, path.Overlay, path.Collision))
    Assert.Equal(None, path.Object)
    let wall = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "soil"), "wall")
    Assert.Equal(("wall", "soil", Some "wall", true), (wall.Type, wall.Background, wall.Object, wall.Collision))
    let door = AuthoringTiles.SetTileLayer(AuthoringTiles.CreateEmptyTile(0.0, 0.0, "wall"), "door")
    Assert.Equal(("door", Some "door", false), (door.Type, door.Object, door.Collision))

[<Fact>]
let ``set tile layer with a visual binds it to the painted layer`` () =
    let tile =
        { AuthoringTiles.CreateEmptyTile(3.0, 4.0, "grass") with
            CustomImage = Some "legacy.png"
            Visuals = Some { TileVisuals.Default with Overlay = Some { VisualRef.Default with AssetId = "old" } } }
    let before = stableOf SchemaJson.encodeTile tile
    let painted = AuthoringTiles.SetTileLayer(tile, "wall", Some { VisualRef.Default with AssetId = "art" })
    Assert.Equal(None, painted.CustomImage)
    Assert.Equal("art", painted.Visuals.Value.Object.Value.AssetId)
    Assert.Equal("old", painted.Visuals.Value.Overlay.Value.AssetId)
    // Without a visual the legacy image stays and the layer's binding clears.
    let plain = AuthoringTiles.SetTileLayer(tile, "path")
    Assert.Equal(Some "legacy.png", plain.CustomImage)
    Assert.Equal(None, plain.Visuals.Value.Overlay)
    Assert.Equal(before, stableOf SchemaJson.encodeTile tile)

[<Fact>]
let ``empty scenes are rows of grass tiles with nothing placed`` () =
    let scene = AuthoringTiles.CreateEmptyScene("scene-x", "X", 4.0, 3.0)
    Assert.Equal(("scene-x", "X", 4.0, 3.0), (scene.Id, scene.Name, scene.Width, scene.Height))
    Assert.Equal(3, scene.Tiles.Length)
    for y in 0 .. 2 do
        Assert.Equal(4, scene.Tiles.[y].Length)
        for x in 0 .. 3 do
            Assert.Equal(AuthoringTiles.CreateEmptyTile(float x, float y, "grass"), scene.Tiles.[y].[x])
    Assert.Empty scene.Transitions
    Assert.Empty scene.Npcs
    Assert.Empty scene.Events
