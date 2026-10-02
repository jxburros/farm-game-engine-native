/// FsCheck properties over the edit layer (docs/LANGUAGES.md "Property tests").
module FarmEngine.Authoring.Tests.PropertyTests

open System.Collections.Generic
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open System.Text.Json.Nodes
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private farmId = "scene-farm"

let private tileTypes = Gen.elements (List.ofSeq TileTypes.All)
let private coord = Gen.choose (-2, 18)
let private inside = Gen.choose (0, 15)

/// Edits that always change the starter project in some way, or not; the property only needs them to be valid edits.
let private simpleEdit : Gen<Edit> =
    Gen.oneof
        [ gen {
              let! x = coord
              let! y = coord
              let! t = tileTypes
              return PaintTiles(farmId, TileRules.layerOf t, [ (x, y) ], t) }
          gen {
              let! x0 = coord
              let! y0 = coord
              let! x1 = coord
              let! y1 = coord
              let! t = tileTypes
              return FillRect(farmId, TileRules.layerOf t, x0, y0, x1, y1, t) }
          gen {
              let! x = coord
              let! y = coord
              let! t = tileTypes
              return FloodFill(farmId, TileRules.layerOf t, x, y, t) }
          gen {
              let! x = inside
              let! y = inside
              return SetCollision(farmId, [ (x, y) ], true) }
          gen {
              let! x = inside
              let! y = inside
              return PlaceNode(farmId, x, y, "node-rock") }
          gen {
              let! w = Gen.choose (1, 24)
              let! h = Gen.choose (1, 24)
              return ResizeScene(farmId, w, h) }
          Gen.constant (RenameScene(farmId, "Renamed"))
          Gen.constant (SetProjectInfo("Property", "0.0.1"))
          Gen.constant (RemoveNpc "npc-farmer")
          Gen.constant (RemoveItem "material-wood")
          Gen.constant (RemoveQuest "quest-first-harvest") ]

let private edits = Arb.fromGen simpleEdit
let private editLists = Arb.fromGen (Gen.listOf simpleEdit)

[<Property(MaxTest = 60)>]
let ``apply then undo returns the original project instance`` () =
    Prop.forAll edits (fun edit ->
        let project = starter ()
        let doc = Document.create project |> Document.apply edit
        obj.ReferenceEquals((Document.undo doc).Project, project))

[<Property(MaxTest = 60)>]
let ``a batch of n edits is one undo step`` () =
    Prop.forAll editLists (fun edits ->
        let project = starter ()
        let doc = Document.create project |> Document.apply (Batch("batch", edits))
        let steps = doc.Past.Length
        (steps = 0 || steps = 1) && (steps = 0 || obj.ReferenceEquals((Document.undo doc).Project, project)))

[<Property(MaxTest = 60)>]
let ``a stroke of edits is one undo step`` () =
    Prop.forAll editLists (fun edits ->
        let project = starter ()
        let doc = edits |> List.fold (fun d e -> Document.applyInStroke "stroke" e d) (Document.create project)
        doc.Past.Length <= 1 && (doc.Past.IsEmpty || obj.ReferenceEquals((Document.undo doc).Project, project)))

[<Property(MaxTest = 60)>]
let ``resize keeps the overlap and fills the rest with grass`` () =
    Prop.forAll (Arb.fromGen (Gen.two (Gen.choose (1, 30)))) (fun (w, h) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (ResizeScene(farmId, w, h)))
        int after.Width = w && int after.Height = h && after.Tiles.Length = h
        && after.Tiles |> Seq.forall (fun row -> row.Length = w)
        && (seq {
                for y in 0 .. h - 1 do
                    for x in 0 .. w - 1 do
                        let t = after.Tiles.[y].[x]
                        if y < before.Tiles.Length && x < before.Tiles.[y].Length then yield obj.ReferenceEquals(t, before.Tiles.[y].[x])
                        else yield t.Type = "grass" && t.X = float x && t.Y = float y && t.SoilState = Some "dry"
            } |> Seq.forall id))

[<Property(MaxTest = 80)>]
let ``flood fill changes only the connected region of the clicked type`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.two coord) tileTypes)) (fun ((x, y), t) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (FloodFill(farmId, TileRules.layerOf t, x, y, t)))
        let inBounds = y >= 0 && y < before.Tiles.Length && x >= 0 && x < before.Tiles.[y].Length
        if not inBounds then obj.ReferenceEquals(before, after)
        else
            let source = before.Tiles.[y].[x].Type
            seq {
                for yy in 0 .. before.Tiles.Length - 1 do
                    for xx in 0 .. before.Tiles.[yy].Length - 1 do
                        let b = before.Tiles.[yy].[xx]
                        let a = after.Tiles.[yy].[xx]
                        // Tiles of another type are untouched; changed tiles were of the source type and now carry the brush.
                        yield (b.Type <> source && obj.ReferenceEquals(a, b)) || (b.Type = source && (obj.ReferenceEquals(a, b) || a.Type = t))
            } |> Seq.forall id)

[<Property(MaxTest = 80)>]
let ``fill rect never touches tiles outside the rectangle or the scene`` () =
    Prop.forAll (Arb.fromGen (Gen.zip (Gen.four coord) tileTypes)) (fun ((x0, y0, x1, y1), t) ->
        let project = starter ()
        let before = farm project
        let after = farm (project |> apply (FillRect(farmId, TileRules.layerOf t, x0, y0, x1, y1, t)))
        after.Tiles.Length = before.Tiles.Length
        && seq {
            for yy in 0 .. before.Tiles.Length - 1 do
                for xx in 0 .. before.Tiles.[yy].Length - 1 do
                    let b = before.Tiles.[yy].[xx]
                    let a = after.Tiles.[yy].[xx]
                    let insideRect = xx >= min x0 x1 && xx <= max x0 x1 && yy >= min y0 y1 && yy <= max y0 y1
                    yield if insideRect then a.Type = t && a.Crop.IsNone && a.Node.IsNone else obj.ReferenceEquals(a, b)
        } |> Seq.forall id)

/// Removing any entity of the starter project leaves no dangling references (no content
/// lint from the C# validator and none of the editor-level reference checks).
let private removals (project: GameProject) : Edit list =
    [ yield! project.Npcs |> Seq.map (fun n -> RemoveNpc n.Id)
      yield! project.Items |> Seq.map (fun i -> RemoveItem i.Id)
      yield! project.Quests |> Seq.map (fun q -> RemoveQuest q.Id)
      yield! project.Shops |> Seq.map (fun s -> RemoveShop s.Id)
      yield! project.Recipes |> Seq.map (fun r -> RemoveRecipe r.Id)
      yield! project.NodeTypes |> Seq.map (fun n -> RemoveNodeType n.Id)
      yield! project.MachineTypes |> Seq.map (fun m -> RemoveMachineType m.Id)
      yield! project.AnimalSpecies |> Seq.map (fun s -> RemoveAnimalSpecies s.Id)
      yield! project.FishTables |> Seq.map (fun f -> RemoveFishTable f.Id)
      yield! project.Actions |> Seq.map (fun a -> RemoveAction a.Id)
      yield! project.Minigames |> Seq.map (fun m -> RemoveMinigame m.Id)
      yield! project.Dialogues |> Seq.map (fun d -> RemoveDialogue d.Id)
      yield! project.Settings.Calendar.Seasons |> Seq.map (fun s -> RemoveSeason s.Id)
      yield! project.CustomAssets |> Seq.map (fun a -> RemoveAsset a.Id) ]

let private referenceCodes (p: Problem) =
    p.Code.StartsWith "content."
    || p.Code.Contains "Unknown" || p.Code.Contains "Missing" || p.Code.Contains "unknown" || p.Code.Contains "missing"
    || p.Code = "dialogue.npcMissing" || p.Code = "quest.giverMissing"

/// References a removal keeps on purpose (#86): a gate on the removed thing (a condition, a
/// required item, a prerequisite, an unlock) stays, so what it gates stays locked, and Problems
/// reports it.
let private keptGate (p: Problem) =
    p.Path.Contains ".conditions["
    || p.Path.EndsWith ".requiresItem"
    || p.Path.EndsWith ".unlock.questId"
    || (p.Code = "content.events" && p.Message.Contains " checks missing ")
    || (p.Code = "content.quests" && p.Message.Contains " requires missing quest ")

[<Property(MaxTest = 40)>]
let ``removing entities leaves no dangling references`` () =
    let project = starter ()
    let all = removals project |> Array.ofList
    Prop.forAll (Arb.fromGen (Gen.listOf (Gen.elements all))) (fun edits ->
        let next = edits |> List.fold (fun p e -> Document.run p e) project
        let dangling =
            Problems.collect next
            |> List.filter referenceCodes
            |> List.filter (fun p -> p.Severity = Severity.Error && not (keptGate p))
        if dangling.IsEmpty then true
        else failwithf "after %A:\n%s" edits (describe dangling))

// ── Migrations ─────────────────────────────────────────────────────────────

/// A change to a fixture project (`fixtures/projects/project-vN.json`) that keeps it a valid
/// project of its own version.
type MigrationVariation =
    | Rename of string
    | Money of int
    /// The scene becomes `width`×`height`, its tiles repeating the fixture's; the player stands
    /// at (`x` mod width, `y` mod height).
    | Regrid of width: int * height: int * x: int * y: int
    | Face of string
    | WallClock of int64
    /// Schema v3+: the calendar date.
    | Calendar of day: int * season: string
    /// Schema v4+: the time of day and year.
    | DayTime of minutes: int * year: int
    /// Schema v6+: an inventory slot of the project's item `index`.
    | Carry of index: int * quantity: int
    | DropSelection
    /// A key no version defines (projects keep unknown keys).
    | Extra of string * int
    /// Reorders the members of the project and its player.
    | Shuffle of seed: int

let private fixtureText =
    let texts = [| for v in 1..8 -> System.IO.File.ReadAllText(testFile [ "Fixtures"; sprintf "project-v%d.json" v ]) |]
    fun (version: int) -> texts[version - 1]

let private migrationVariation : Gen<MigrationVariation> =
    let text = Gen.elements [ "Farm"; "Ferme d'été"; "Hof ☀"; "  spaced  "; "x"; "农场" ]
    Gen.oneof
        [ Gen.map Rename text
          Gen.map Money (Gen.choose (0, 1_000_000))
          Gen.map4 (fun w h x y -> Regrid(w, h, x, y)) (Gen.choose (1, 7)) (Gen.choose (1, 6)) (Gen.choose (0, 20)) (Gen.choose (0, 20))
          Gen.map Face (Gen.elements [ "up"; "down"; "left"; "right" ])
          Gen.map (fun n -> WallClock(int64 n * 1000L)) (Gen.choose (0, 2_000_000_000))
          Gen.map2 (fun d s -> Calendar(d, s)) (Gen.choose (1, 112)) (Gen.elements [ "spring"; "summer"; "fall"; "winter" ])
          Gen.map2 (fun m y -> DayTime(m, y)) (Gen.choose (360, 1500)) (Gen.choose (1, 9))
          Gen.map2 (fun i q -> Carry(i, q)) (Gen.choose (0, 5)) (Gen.choose (1, 99))
          Gen.constant DropSelection
          Gen.map2 (fun k v -> Extra(k, v)) (Gen.elements [ "customField"; "x-note" ]) (Gen.choose (0, 9))
          Gen.map Shuffle (Gen.choose (0, 1000)) ]

let private reorder (seed: int) (o: JsonObject) =
    let members = [ for KeyValue(k, v) in List.ofSeq o -> k, v ]
    let random = System.Random(seed)
    o.Clear()
    for (k, v) in members |> List.sortBy (fun _ -> random.Next()) do
        o[k] <- v

let private vary (version: int) (project: JsonObject) (variation: MigrationVariation) =
    let player = (project["player"] |> nonNull).AsObject()
    let scene = ((project["scenes"] |> nonNull).[0] |> nonNull).AsObject()
    match variation with
    | Rename name -> project["name"] <- JsonValue.Create name
    | Money money -> player["money"] <- JsonValue.Create money
    | Regrid(w, h, x, y) ->
        let tiles = [| for row in (scene["tiles"] |> nonNull).AsArray() do for tile in (row |> nonNull).AsArray() -> tile |> nonNull |]
        let grid = JsonArray()
        for ty in 0 .. h - 1 do
            let row = JsonArray()
            for tx in 0 .. w - 1 do
                let tile = tiles[(ty * w + tx) % tiles.Length].DeepClone().AsObject()
                tile["x"] <- JsonValue.Create tx
                tile["y"] <- JsonValue.Create ty
                row.Add tile
            grid.Add row
        scene["tiles"] <- grid
        scene["width"] <- JsonValue.Create w
        scene["height"] <- JsonValue.Create h
        player["x"] <- JsonValue.Create(x % w)
        player["y"] <- JsonValue.Create(y % h)
    | Face direction -> player["direction"] <- JsonValue.Create direction
    | WallClock ms ->
        project["currentTime"] <- JsonValue.Create ms
        if version >= 3 then project["gameStartTime"] <- JsonValue.Create ms
    | Calendar(day, season) when version >= 3 ->
        project["currentDay"] <- JsonValue.Create day
        project["currentSeason"] <- JsonValue.Create season
    | DayTime(minutes, year) when version >= 4 ->
        project["currentTimeMinutes"] <- JsonValue.Create minutes
        project["currentYear"] <- JsonValue.Create year
    | Carry(index, quantity) when version >= 6 ->
        let items = (project["items"] |> nonNull).AsArray()
        let slot = JsonObject()
        slot["item"] <- (items[index % items.Count] |> nonNull).DeepClone()
        slot["quantity"] <- JsonValue.Create quantity
        (player["inventory"] |> nonNull).AsArray().Add slot
    | DropSelection ->
        project.Remove "selectedNPCId" |> ignore
        project.Remove "selectedItemId" |> ignore
    | Extra(key, value) -> project[key] <- JsonValue.Create value
    | Shuffle seed ->
        reorder seed project
        reorder (seed + 1) player
    | Calendar _ | DayTime _ | Carry _ -> ()

let private variedFixture (version: int) (variations: MigrationVariation list) : string =
    let project = (JsonNode.Parse(fixtureText version) |> nonNull).AsObject()
    for variation in variations do
        vary version project variation
    project.ToJsonString()

[<Property(MaxTest = 150)>]
let ``migrating a vN project gives a project that passes the schema checks of the current version`` () =
    let cases = Gen.zip (Gen.choose (1, 8)) (Gen.listOf migrationVariation)
    Prop.forAll (Arb.fromGen cases) (fun (version, variations) ->
        let result = ProjectMigrations.migrateProjectText (variedFixture version variations)
        if not result.Ok then failwithf "v%d did not migrate: %A" version (List.ofSeq result.Errors)
        let project = result.Data.Value
        let issues = SchemaChecks.projectIssues project
        if not issues.IsEmpty then failwithf "v%d migrated with schema issues: %A" version issues
        // Migrating the result again changes nothing: it is a current project.
        let stable = stableOf SchemaJson.encodeGameProject project
        let again = ProjectMigrations.migrateProjectText stable
        project.SchemaVersion = ProjectSchema.CurrentProjectSchemaVersion
        && result.FromVersion = float version
        && result.Migrated = (version < 9)
        && again.Ok
        && not again.Migrated
        && stableOf SchemaJson.encodeGameProject again.Data.Value = stable)

// ── Compiling ──────────────────────────────────────────────────────────────

/// One authoring step on the current scene (the player's, or the start scene): an edit, or a
/// Creator Workshop pattern. Indexes pick among the project's current content.
type AuthoringStep =
    | Paint of x: int * y: int * tileType: string
    | Fill of x0: int * y0: int * x1: int * y1: int * tileType: string
    | Flood of x: int * y: int * tileType: string
    | Collide of x: int * y: int * blocked: bool
    | Node of x: int * y: int * index: int
    | Machine of x: int * y: int * index: int
    | Resize of width: int * height: int
    | PlayerStart of x: int * y: int
    | Stock of index: int
    | Remove of index: int
    | Pattern of PatternKind * name: string * x: int * y: int * day: int * friendship: int * consequences: bool

let private authoringStep : Gen<AuthoringStep> =
    let c = Gen.choose (-1, 20)
    let index = Gen.choose (0, 200)
    Gen.frequency
        [ 3, Gen.map3 (fun x y t -> Paint(x, y, t)) c c tileTypes
          1, gen {
                 let! x0 = c
                 let! y0 = c
                 let! x1 = c
                 let! y1 = c
                 let! t = tileTypes
                 return Fill(x0, y0, x1, y1, t) }
          1, Gen.map3 (fun x y t -> Flood(x, y, t)) c c tileTypes
          1, Gen.map3 (fun x y b -> Collide(x, y, b)) c c (Gen.elements [ true; false ])
          2, Gen.map3 (fun x y i -> Node(x, y, i)) c c index
          1, Gen.map3 (fun x y i -> Machine(x, y, i)) c c index
          1, Gen.map2 (fun w h -> Resize(w, h)) (Gen.choose (4, 24)) (Gen.choose (4, 24))
          1, Gen.map2 (fun x y -> PlayerStart(x, y)) c c
          1, Gen.map Stock index
          2, Gen.map Remove index
          4, gen {
                 let! kind = Gen.elements (Patterns.all |> List.map (fun p -> p.Kind))
                 let! name = Gen.elements [ ""; "Old Oak"; "Mira"; "Spell of Rain"; "Tree"; "tree" ]
                 let! x = c
                 let! y = c
                 let! day = Gen.choose (1, 30)
                 let! friendship = Gen.choose (0, 1000)
                 let! consequences = Gen.elements [ true; false ]
                 return Pattern(kind, name, x, y, day, friendship, consequences) } ]

let private pick (items: seq<'T>) (index: int) : 'T option =
    let array = Array.ofSeq items
    if array.Length = 0 then None else Some array[index % array.Length]

/// The scene the workshop edits: the player's, else the start scene, else the first.
let private currentScene (project: GameProject) : Scene option =
    let byId id = project.Scenes |> Seq.tryFind (fun s -> s.Id = id)
    byId project.Player.SceneId
    |> Option.orElse (byId project.StartSceneId)
    |> Option.orElse (Seq.tryHead project.Scenes)

let private author (project: GameProject) (step: AuthoringStep) : GameProject =
    match currentScene project with
    | None -> project
    | Some scene ->
        let id = scene.Id
        let run edit = Document.run project edit
        let runSome = Option.map run >> Option.defaultValue project
        match step with
        | Paint(x, y, t) -> run (PaintTiles(id, TileRules.layerOf t, [ (x, y) ], t))
        | Fill(x0, y0, x1, y1, t) -> run (FillRect(id, TileRules.layerOf t, x0, y0, x1, y1, t))
        | Flood(x, y, t) -> run (FloodFill(id, TileRules.layerOf t, x, y, t))
        | Collide(x, y, blocked) -> run (SetCollision(id, [ (x, y) ], blocked))
        | Node(x, y, i) -> pick project.NodeTypes i |> Option.map (fun n -> PlaceNode(id, x, y, n.Id)) |> runSome
        | Machine(x, y, i) -> pick project.MachineTypes i |> Option.map (fun m -> PlaceMachine(id, x, y, m.Id)) |> runSome
        | Resize(w, h) -> run (ResizeScene(id, w, h))
        | PlayerStart(x, y) -> run (SetPlayerStart(id, x, y))
        | Stock i -> pick project.Items i |> Option.map (fun item -> AddToInventory item.Id) |> runSome
        | Remove i -> pick (removals project) i |> runSome
        | Pattern(kind, name, x, y, day, friendship, consequences) ->
            let npcId = project.Npcs |> Seq.tryHead |> Option.map (fun n -> n.Id) |> Option.defaultValue ""
            let options =
                { Name = name; Text = ""; X = x; Y = y; Day = day; NpcId = npcId; Friendship = friendship; Consequences = consequences; SceneId = "" }
            match Patterns.build kind options project with
            | Ok edit -> run edit
            | Error _ -> project

[<Property(MaxTest = 100, MaxRejected = 400)>]
let ``compiling a project with no errors in Problems never throws`` () =
    let cases = Gen.zip (Gen.elements (List.ofSeq ProjectCatalog.All)) (Gen.listOf authoringStep)
    Prop.forAll (Arb.fromGen cases) (fun (template, steps) ->
        let project = steps |> List.fold author (ProjectCatalog.CreateProjectForTemplate(template, 0.0))
        (errors project).IsEmpty
        ==> lazy
            (let cart = CartridgeCompiler.Compile project
             let content = ContentCompiler.compile project
             cart.Length > 0 && not (isNull (box content))))
