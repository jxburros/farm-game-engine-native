/// The schema records' JSON codecs (ported from the retired C# FarmEngine.Schemas tests): defaults
/// for absent keys, undeclared keys kept on passthrough records and dropped elsewhere, the
/// condition union and a lossless round trip of the v8 fixture.
module FarmEngine.Authoring.Tests.SchemaRecordTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error message -> failwith message

let private decode (decoder: Path -> Json -> 'T) (text: string) : 'T = decodeJson decoder (parse text)

let private encoded (encode: 'T -> Json) (value: 'T) = Json.stringify (encode value)

// crafting.test.ts
[<Fact>]
let ``recipes default to the crafting category and no station`` () =
    let recipe = decode SchemaJson.decodeRecipeDefinition """{"id":"recipe-x","name":"X","inputs":[],"outputs":[]}"""
    Assert.Equal("crafting", recipe.Category)
    Assert.Equal(None, recipe.RequiresStationCategory)
    let explicitRecipe =
        decode SchemaJson.decodeRecipeDefinition
            """{"id":"recipe-y","name":"Y","inputs":[],"outputs":[],"category":"magic","requiresStationCategory":"arcane-circle"}"""
    Assert.Equal(("magic", Some "arcane-circle"), (explicitRecipe.Category, explicitRecipe.RequiresStationCategory))

[<Fact>]
let ``machine types default to no station categories`` () =
    Assert.Empty((decode SchemaJson.decodeMachineTypeDefinition """{"id":"machine-x","name":"X"}""").StationCategories)
    let kitchen = decode SchemaJson.decodeMachineTypeDefinition """{"id":"machine-kitchen","name":"Kitchen","stationCategories":["cooking"]}"""
    Assert.Equal<string list>([ "cooking" ], kitchen.StationCategories)

// assets.test.ts
[<Fact>]
let ``sprite sheets apply the slicing defaults and plain assets stay static`` () =
    let sheet = decode SchemaJson.decodeSpriteSheet """{ "frameWidth": 32, "frameHeight": 32, "frames": 4 }"""
    Assert.Equal(6.0, sheet.TicksPerFrame)
    Assert.True sheet.Directional
    let asset = decode SchemaJson.decodeCustomAsset """{ "id": "a1", "name": "Hero", "type": "player", "dataUrl": "data:image/png;base64,x" }"""
    Assert.Equal(None, asset.Sheet)
    Assert.DoesNotContain("\"sheet\"", encoded SchemaJson.encodeCustomAsset asset)

[<Fact>]
let ``the v8 fixture round trips stably and keeps present-as-null keys`` () =
    let json = readJson [ "Fixtures"; "project-v8.json" ]
    let project = projectOf json
    let first = Json.stringify (ProjectLoad.toJson project)
    let again = roundTrip project
    Assert.Equal(first, Json.stringify (ProjectLoad.toJson again))
    Assert.Equal(project, again)
    // Every key of the source document is kept.
    let rec keys (prefix: string) (value: Json) : string list =
        match value with
        | JObject members -> [ for key, child in members do yield prefix + key; yield! keys (prefix + key + ".") child ]
        | JArray items -> items |> List.mapi (fun i child -> keys (prefix + string i + ".") child) |> List.concat
        | _ -> []
    let written = Set.ofList (keys "" (ProjectLoad.toJson project))
    let missing = keys "" json |> List.filter (fun key -> not (written.Contains key))
    Assert.True(missing.IsEmpty, String.concat "\n" (List.truncate 20 missing))
    Assert.Contains("\"selectedNPCId\":null", first)
    Assert.Contains("\"overlay\":null", first)

[<Fact>]
let ``undeclared keys survive on passthrough records`` () =
    let json =
        match readJson [ "Fixtures"; "project-v8.json" ] with
        | JObject members -> JObject(members @ [ "futureField", parse """{"nested":42}""" ])
        | _ -> failwith "not an object"
    let project = projectOf json
    Assert.Equal(parse """{"nested":42}""", field "futureField" project.Extra)
    let withGlow =
        let tile = { project.Scenes.[0].Tiles.[0].[0] with Extra = [ "glow", JString "blue" ] }
        { project with Scenes = { project.Scenes.[0] with Tiles = (tile :: project.Scenes.[0].Tiles.[0].Tail) :: project.Scenes.[0].Tiles.Tail } :: project.Scenes.Tail }
    let reparsed = roundTrip withGlow
    Assert.Equal(parse """{"nested":42}""", field "futureField" reparsed.Extra)
    Assert.Equal(JString "blue", field "glow" reparsed.Scenes.[0].Tiles.[0].[0].Extra)
    Assert.Equal(withGlow, reparsed)

[<Fact>]
let ``records without passthrough drop undeclared keys`` () =
    let graphics = decode SchemaJson.decodeGraphicsSettings """{"pixelArt":false,"bogus":1}"""
    Assert.Equal("""{"pixelArt":false}""", encoded SchemaJson.encodeGraphicsSettings graphics)
    let condition = decode SchemaJson.decodeEventCondition """{"type":"flag","flag":"f","junk":"x"}"""
    Assert.Equal("""{"type":"flag","flag":"f","value":true}""", encoded SchemaJson.encodeEventCondition condition)

[<Fact>]
let ``conditions decode to their case with defaults`` () =
    match decode SchemaJson.decodeEventCondition """{"type":"hasItem","itemId":"wheat"}""" with
    | EventCondition.HasItem hasItem -> Assert.Equal(1.0, hasItem.Quantity)
    | other -> failwithf "unexpected %A" other
    match decode SchemaJson.decodeEventCondition """{"flag":"f","type":"flag"}""" with
    | EventCondition.Flag flag -> Assert.True flag.Value
    | other -> failwithf "unexpected %A" other
    let enter = decode SchemaJson.decodeEventCondition """{"type":"enterTile","x":1,"y":2}"""
    Assert.Equal("""{"type":"enterTile","x":1,"y":2}""", encoded SchemaJson.encodeEventCondition enter)
    let event =
        decode SchemaJson.decodeGameEvent
            """{"id":"e","name":"E","sceneId":"","trigger":"enter","conditions":[{"type":"season","seasons":["winter"]},{"type":"friendship","npcId":"bob","min":10}],"outcomes":[{"type":"message","message":"hi"}],"active":true,"repeatable":false}"""
    Assert.Equal("season", event.Conditions.[0].Type)
    match event.Conditions.[1] with
    | EventCondition.Friendship friendship -> Assert.Equal(10.0, friendship.Min)
    | other -> failwithf "unexpected %A" other
    Assert.Equal(EventOutcomeTypes.Message, event.Outcomes.[0].Type)

[<Fact>]
let ``an unknown condition type is refused`` () =
    match Decode.run SchemaJson.decodeEventCondition (parse """{"type":"nope"}""") with
    | Error _ -> ()
    | Ok condition -> failwithf "decoded %A" condition
