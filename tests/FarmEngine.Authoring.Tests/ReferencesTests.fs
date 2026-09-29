module FarmEngine.Authoring.Tests.ReferencesTests

open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open Microsoft.FSharp.Reflection
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

// ---- The declarations cover the schema (reflection over the F# records) ----

let private schemaAssembly = typeof<GameProject>.Assembly

/// A record's form fields: every declared field but the undeclared-keys bag.
let private formProperties (t: Type) : PropertyInfo list =
    FSharpType.GetRecordFields t |> Array.filter (fun p -> p.Name <> "Extra") |> List.ofArray

let private isGeneric (definition: Type) (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = definition

/// `'T option` → `'T`; anything else as it is.
let private unwrapOption (t: Type) = if isGeneric typedefof<option<_>> t then t.GetGenericArguments().[0] else t

/// Every schema record a project can hold: the types reachable from `GameProject`, including the
/// condition records inside the `EventCondition` union.
let private reachableRecords () =
    let seen = HashSet<Type>()
    let rec walk (t: Type) =
        if t.IsGenericType then for argument in t.GetGenericArguments() do walk argument
        elif t = typeof<Json> then ()
        elif t.Assembly = schemaAssembly && FSharpType.IsUnion t && seen.Add t then
            for case in FSharpType.GetUnionCases t do
                for field in case.GetFields() do walk field.PropertyType
        elif t.Assembly = schemaAssembly && FSharpType.IsRecord t && seen.Add t then
            for p in formProperties t do walk p.PropertyType
    walk typeof<GameProject>
    seen |> Seq.filter FSharpType.IsRecord |> List.ofSeq

let private isStringList (t: Type) = unwrapOption t = typeof<string list>

/// An ordered map keyed by strings: `(string * 'T) list`.
let private isStringDictionary (t: Type) =
    let t = unwrapOption t
    isGeneric typedefof<list<_>> t
    && (let element = t.GetGenericArguments().[0]
        FSharpType.IsTuple element && (FSharpType.GetTupleElements element).[0] = typeof<string>)

let private isText (t: Type) = unwrapOption t = typeof<string>

let private referenceNames =
    References.declarations
    |> List.choose (fun d ->
        match d.Role with
        | FieldRole.Reference _ | FieldRole.ReferenceList _ | FieldRole.ReferenceKeys _ -> Some d.Property
        | _ -> None)
    |> Set.ofList

/// A property that looks like it holds ids: `*Id`/`*Ids`, a name used for a reference anywhere,
/// any list of strings and any map keyed by strings. `Id` itself is the record's own id.
let private looksLikeReference (p: PropertyInfo) =
    let t = p.PropertyType
    p.Name <> "Id"
    && ((isText t && (p.Name.EndsWith "Id" || referenceNames.Contains p.Name))
        || isStringList t
        || isStringDictionary t)

let private recordNamed (name: string) =
    schemaAssembly.GetTypes() |> Array.tryFind (fun t -> t.Namespace = "FarmEngine.Schemas" && t.Name = name && FSharpType.IsRecord t)

[<Fact>]
let ``every id-like property of every reachable schema record is declared`` () =
    let records = reachableRecords ()
    Assert.Contains(typeof<ShopStockEntry>, records)
    Assert.Contains(typeof<FriendshipCondition>, records)
    let undeclared =
        [ for t in records do
              for p in formProperties t do
                  if looksLikeReference p && (References.roleOf t.Name p.Name).IsNone then
                      yield sprintf "%s.%s" t.Name p.Name ]
    Assert.True(undeclared.IsEmpty, "Declare these in References.declarations (a reference or NotReference):\n" + String.Join("\n", undeclared))

[<Fact>]
let ``every declaration names a real property of the right shape`` () =
    let problems =
        [ for d in References.declarations do
              match recordNamed d.Owner with
              | None -> yield sprintf "%s: no such record" d.Owner
              | Some t ->
                  match formProperties t |> List.tryFind (fun p -> p.Name = d.Property) with
                  | None -> yield sprintf "%s.%s: no such property" d.Owner d.Property
                  | Some p ->
                      let fits =
                          match d.Role with
                          | FieldRole.Reference _ | FieldRole.OneOf _ -> isText p.PropertyType
                          | FieldRole.ReferenceList _ -> isStringList p.PropertyType
                          | FieldRole.ReferenceKeys _ -> isStringDictionary p.PropertyType
                          | FieldRole.NotReference reason -> reason.Length > 0
                      if not fits then yield sprintf "%s.%s: role %A does not fit %s" d.Owner d.Property d.Role p.PropertyType.Name ]
    Assert.True(problems.IsEmpty, String.Join("\n", problems))
    let duplicates = References.declarations |> List.countBy (fun d -> d.Owner, d.Property) |> List.filter (fun (_, n) -> n > 1)
    Assert.Empty duplicates

[<Fact>]
let ``optional references offer an empty entry`` () =
    let missingEmpty =
        [ for d in References.declarations do
              match d.Role with
              | FieldRole.Reference(_, None) ->
                  let t = (recordNamed d.Owner).Value
                  let p = formProperties t |> List.find (fun p -> p.Name = d.Property)
                  if isGeneric typedefof<option<_>> p.PropertyType then yield sprintf "%s.%s" d.Owner d.Property
              | _ -> () ]
    Assert.True(missingEmpty.IsEmpty, String.Join("\n", missingEmpty))

[<Fact>]
let ``key references point at the right kinds`` () =
    let kindOf owner property =
        match References.roleOf owner property with
        | Some(FieldRole.Reference(kind, _)) | Some(FieldRole.ReferenceList kind) | Some(FieldRole.ReferenceKeys kind) -> Some kind
        | _ -> None
    Assert.Equal(Some ReferenceKind.Item, kindOf "ShopStockEntry" "ItemId")
    Assert.Equal(Some ReferenceKind.Item, kindOf "RecipeIngredient" "ItemId")
    Assert.Equal(Some ReferenceKind.Item, kindOf "MachineTypeDefinition" "ItemId")
    Assert.Equal(Some ReferenceKind.Npc, kindOf "Dialogue" "NpcId")
    Assert.Equal(Some ReferenceKind.Item, kindOf "QuestObjective" "TargetItemId")
    Assert.Equal(Some ReferenceKind.Npc, kindOf "QuestObjective" "TargetNpcId")
    Assert.Equal(Some ReferenceKind.Scene, kindOf "QuestObjective" "TargetSceneId")
    Assert.Equal(Some ReferenceKind.Crop, kindOf "QuestObjective" "TargetCropType")
    Assert.Equal(Some ReferenceKind.Scene, kindOf "NpcScheduleEntry" "SceneId")
    Assert.Equal(Some ReferenceKind.Item, kindOf "GiftTastes" "Loved")
    Assert.Equal(Some ReferenceKind.Item, kindOf "AnimalSpeciesDefinition" "ProductItemId")
    Assert.Equal(Some ReferenceKind.Item, kindOf "FishTableEntry" "ItemId")
    Assert.Equal(Some ReferenceKind.Action, kindOf "EventOutcome" "ActionId")
    Assert.Equal(Some ReferenceKind.Minigame, kindOf "EventOutcome" "MinigameId")
    Assert.Equal(Some ReferenceKind.Season, kindOf "WeatherConfig" "Table")
    Assert.Equal(Some ReferenceKind.Skill, kindOf "RecipeSkillRequirement" "Skill")
    Assert.Equal(Some ReferenceKind.Asset, kindOf "ExportSettings" "IconAssetId")
    match References.roleOf "EventOutcome" "SoundId" with
    | Some(FieldRole.NotReference _) -> ()
    | other -> failwithf "sound ids are free text, got %A" other
    for kind in ReferenceKind.all do
        Assert.Equal(Some kind, ReferenceKind.tryParse (ReferenceKind.name kind))

// ---- Picker options come from the compiler ----

let private ids (options: PickerOption list) = options |> List.map (fun o -> o.Id)

[<Fact>]
let ``item options are the items the game resolves`` () =
    let project = starter ()
    Assert.Equal<string list>(project.Items |> Seq.map (fun i -> i.Id) |> List.ofSeq, ids (References.options ReferenceKind.Item project))
    let bare = { project with Items = [] }
    Assert.Equal<string list>(Builtin.items () |> Seq.map (fun i -> i.Id) |> List.ofSeq, ids (References.options ReferenceKind.Item bare))
    let first = References.options ReferenceKind.Item project |> List.head
    Assert.Equal(sprintf "%s (%s)" project.Items.[0].Name project.Items.[0].Id, first.Label)

[<Fact>]
let ``crop, node type, weather and calendar options merge the built-in catalog`` () =
    let project = starter ()
    let crop = Defaults.newCrop project
    let withCrop = project |> apply (UpsertCrop crop)
    let crops = ids (References.options ReferenceKind.Crop withCrop)
    Assert.Equal<string list>(List.ofSeq PrimitivesSchema.BuiltinCropIds @ [ crop.Id ], crops)
    let node = Defaults.newNodeType project
    let nodes = ids (References.options ReferenceKind.NodeType (project |> apply (UpsertNodeType node)))
    Assert.Contains("node-tree", nodes)
    Assert.Contains("node-copper-ore", nodes)
    Assert.Equal(node.Id, List.last nodes)
    Assert.Equal<string list>(project.Settings.Calendar.Seasons |> Seq.map (fun s -> s.Id) |> List.ofSeq, ids (References.options ReferenceKind.Season project))
    Assert.Equal<string list>(project.Weather.Types |> Seq.map (fun w -> w.Id) |> List.ofSeq, ids (References.options ReferenceKind.Weather project))
    Assert.Equal<string list>(List.ofSeq SaveSchema.SkillNames, ids (References.options ReferenceKind.Skill project))

[<Fact>]
let ``dialogue options include every NPC's dialogue once`` () =
    let project = starter ()
    let options = References.options ReferenceKind.Dialogue project
    let expected = project.Npcs |> Seq.collect (fun n -> n.Dialogue) |> Seq.map (fun d -> d.Id) |> Seq.distinct |> List.ofSeq
    Assert.Equal<string list>(expected, ids options |> List.filter (fun id -> List.contains id expected))
    Assert.Equal(options.Length, (ids options |> List.distinct).Length)
    Assert.StartsWith((npc project "npc-farmer").Name + ": ", (options |> List.find (fun o -> o.Id = "dialogue-farmer-greeting")).Label)

[<Fact>]
let ``pickers keep a missing id and offer the empty entry`` () =
    let project = starter ()
    let entries = References.picker ReferenceKind.Shop (Some "(none)") project (Some "shop-gone")
    Assert.Equal("", entries.[0].Id)
    Assert.Equal("(none)", entries.[0].Label)
    Assert.Equal("shop-gone", entries.[1].Id)
    Assert.Equal("(missing: shop-gone)", entries.[1].Label)
    Assert.True entries.[1].Missing
    let known = References.picker ReferenceKind.Item None project (Some project.Items.[0].Id)
    Assert.DoesNotContain(known, fun o -> o.Missing)
    Assert.Equal(project.Items.Length, known.Length)
    Assert.Equal(project.Items.Length, (References.picker ReferenceKind.Item None project None).Length)

// ---- The condition/outcome vocabulary matches event-vocabulary.ts and event-forms.tsx ----

[<Fact>]
let ``condition and outcome types are the web lists`` () =
    Assert.Equal<string list>(
        [ "enterTile"; "interactTile"; "friendship"; "weather"; "inventorySpace"; "hasItem"; "flag"; "dayRange"; "season"; "yearRange"; "timeOfDay"; "questStatus"; "festivalId" ],
        Vocabulary.conditionTypes |> List.map fst)
    Assert.Equal("Has item ≥ n", Vocabulary.conditionLabel "hasItem")
    Assert.Equal<string list>(
        [ "modifyFriendship"; "modifyEnergy"; "waterArea"; "message"; "giveItem"; "takeItem"; "giveMoney"; "takeMoney"; "setFlag"; "clearFlag"
          "startQuest"; "completeQuest"; "spawnNPC"; "removeNPC"; "changeTile"; "warpPlayer"; "startDialogue"; "lockTransition"; "unlockTransition"
          "playSound"; "performAction"; "startMinigame" ],
        Vocabulary.outcomeTypes |> List.map fst)
    Assert.Equal("Water nearby soil (magic)", Vocabulary.outcomeLabel "waterArea")
    Assert.Equal("unlockScene", Vocabulary.outcomeLabel "unlockScene")
    // Every outcome type the vocabulary offers is a schema outcome type.
    for kind, _ in Vocabulary.outcomeTypes do
        Assert.Contains(kind, EventOutcomeTypes.All)

[<Fact>]
let ``each condition type shows the web fields`` () =
    let keys kind = Vocabulary.conditionFields kind |> List.map (fun f -> f.Key)
    let expected =
        [ "enterTile", [ "x"; "y"; "x2"; "y2" ]
          "interactTile", [ "x"; "y"; "x2"; "y2" ]
          "friendship", [ "npcId"; "min" ]
          "weather", [ "weatherIds" ]
          "inventorySpace", [ "itemId"; "quantity" ]
          "hasItem", [ "itemId"; "quantity" ]
          "flag", [ "flag"; "value" ]
          "dayRange", [ "minDay"; "maxDay" ]
          "season", [ "seasons" ]
          "yearRange", [ "minYear"; "maxYear" ]
          "timeOfDay", [ "minMinute"; "maxMinute" ]
          "questStatus", [ "questId"; "status" ]
          "festivalId", [ "festivalId" ] ]
    for kind, fields in expected do
        Assert.Equal<string list>(fields, keys kind)
    Assert.Equal(expected.Length, Vocabulary.conditionTypes.Length)
    let x2 = Vocabulary.conditionFields "enterTile" |> List.find (fun f -> f.Key = "x2")
    Assert.True x2.Optional
    Assert.Equal("X2 (opt)", x2.Label)
    let quantity = Vocabulary.conditionFields "hasItem" |> List.find (fun f -> f.Key = "quantity")
    Assert.Equal(1.0, quantity.WhenEmpty)
    Assert.Equal(FieldKind.Reference ReferenceKind.Npc, (Vocabulary.conditionFields "friendship" |> List.head).Kind)
    Assert.Equal(FieldKind.ReferenceList ReferenceKind.Weather, (Vocabulary.conditionFields "weather" |> List.head).Kind)

[<Fact>]
let ``each outcome type shows the web fields`` () =
    let keys kind = Vocabulary.outcomeFields kind |> List.map (fun f -> f.Key)
    let expected =
        [ "message", [ "message" ]
          "giveItem", [ "itemId"; "itemQuantity" ]
          "takeItem", [ "itemId"; "itemQuantity" ]
          "giveMoney", [ "amount" ]
          "takeMoney", [ "amount" ]
          "modifyEnergy", [ "amount" ]
          "waterArea", [ "radius" ]
          "modifyFriendship", [ "npcId"; "amount" ]
          "setFlag", [ "flagName" ]
          "clearFlag", [ "flagName" ]
          "startQuest", [ "questId" ]
          "completeQuest", [ "questId" ]
          "spawnNPC", [ "npcId"; "x"; "y" ]
          "removeNPC", [ "npcId" ]
          "startDialogue", [ "npcId" ]
          "changeTile", [ "tileX"; "tileY"; "newTileType" ]
          "warpPlayer", [ "sceneId"; "x"; "y" ]
          "lockTransition", [ "sceneId"; "x"; "y" ]
          "unlockTransition", [ "sceneId"; "x"; "y" ]
          "playSound", [ "soundId" ]
          "performAction", [ "actionId" ]
          "startMinigame", [ "minigameId" ] ]
    for kind, fields in expected do
        Assert.Equal<string list>(fields, keys kind)
    Assert.Equal(expected.Length, Vocabulary.outcomeTypes.Length)
    Assert.Empty(keys "unlockScene")
    let radius = Vocabulary.outcomeFields "waterArea" |> List.head
    Assert.Equal((Some 0.0, Some 10.0, 1.0), (radius.Min, radius.Max, radius.WhenEmpty))
    Assert.Equal("From X", (Vocabulary.outcomeFields "lockTransition").[1].Label)
    Assert.True((Vocabulary.outcomeFields "spawnNPC").[1].Optional)

[<Fact>]
let ``condition defaults match event-vocabulary.ts`` () =
    let project = starter ()
    let json kind = Json.stringify (SchemaJson.encodeEventCondition (Vocabulary.defaultCondition kind project))
    let season = project.Settings.Calendar.Seasons.[0].Id
    let festival = match Seq.tryHead project.Settings.Calendar.Festivals with Some f -> f.Id | None -> ""
    let expected =
        [ "enterTile", """{"type":"enterTile","x":0,"y":0}"""
          "interactTile", """{"type":"interactTile","x":0,"y":0}"""
          "inventorySpace", """{"type":"inventorySpace","itemId":"","quantity":1}"""
          "hasItem", """{"type":"hasItem","itemId":"","quantity":1}"""
          "flag", """{"type":"flag","flag":"","value":true}"""
          "dayRange", """{"type":"dayRange","minDay":1}"""
          "season", sprintf """{"type":"season","seasons":["%s"]}""" season
          "yearRange", """{"type":"yearRange","minYear":1}"""
          "timeOfDay", """{"type":"timeOfDay","minMinute":360,"maxMinute":720}"""
          "questStatus", """{"type":"questStatus","questId":"","status":"completed"}"""
          "friendship", """{"type":"friendship","npcId":"","min":250}"""
          "weather", """{"type":"weather","weatherIds":["rain"]}"""
          "festivalId", sprintf """{"type":"festivalId","festivalId":"%s"}""" festival
          "nonsense", """{"type":"flag","flag":"","value":true}""" ]
    for kind, text in expected do
        Assert.Equal(text, json kind)
    let withFestival =
        let calendar = { project.Settings.Calendar with Festivals = ([ { CalendarFestival.Default with Id = "fair"; Name = "Fair"; SeasonId = season; Day = 1.0 } ]) }
        project |> apply (SetSettings({ project.Settings with Calendar = calendar }))
    match Vocabulary.defaultCondition "festivalId" withFestival with
    | EventCondition.FestivalId condition -> Assert.Equal("fair", condition.FestivalId)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``outcome defaults are just the type, like the web add picker`` () =
    for kind, _ in Vocabulary.outcomeTypes do
        Assert.Equal(sprintf """{"type":"%s"}""" kind, Json.stringify (SchemaJson.encodeEventOutcome (Vocabulary.defaultOutcome kind)))

[<Fact>]
let ``quest objectives show the targets of their type`` () =
    Assert.Equal<string list>([ "TargetItemId"; "TargetItemQuantity" ], Vocabulary.objectiveTargets "collect")
    Assert.Equal<string list>([ "TargetCropType"; "TargetCropQuantity"; "TargetNpcId"; "TargetSceneId" ], Vocabulary.hiddenProperties "QuestObjective" "craft")
    Assert.Equal<string list>([ "TargetItemId"; "TargetItemQuantity"; "TargetNpcId"; "TargetSceneId" ], Vocabulary.hiddenProperties "QuestObjective" "harvest")
    Assert.Empty(Vocabulary.hiddenProperties "Item" "seed")

[<Fact>]
let ``new list rows start like the web add buttons`` () =
    let project = starter ()
    let firstItem = project.Items.[0].Id
    let stock = Vocabulary.newElement "ShopStockEntry" project (box project) [] |> Option.get :?> ShopStockEntry
    Assert.Equal(firstItem, stock.ItemId)
    let ingredient = Vocabulary.newElement "RecipeIngredient" project (box project) [] |> Option.get :?> RecipeIngredient
    Assert.Equal((firstItem, 1.0), (ingredient.ItemId, ingredient.Quantity))
    let objective = Vocabulary.newElement "QuestObjective" project (box project) [ "obj-1"; "obj-2" ] |> Option.get :?> QuestObjective
    Assert.Equal("obj-3", objective.Id)
    let farmer = npc project "npc-farmer"
    let stop = Vocabulary.newElement "NpcScheduleEntry" project (box farmer) [] |> Option.get :?> NpcScheduleEntry
    Assert.Equal((farmer.SceneId, 480.0), (stop.SceneId, stop.Minute))
    let dialogue = Vocabulary.newElement "Dialogue" project (box farmer) [ "dialogue-1" ] |> Option.get :?> Dialogue
    Assert.Equal(farmer.Id, dialogue.NpcId)
    Assert.DoesNotContain(dialogue.Id, Defaults.allIds project)
    Assert.NotEqual<string>("dialogue-1", dialogue.Id)
    Assert.True((Vocabulary.newElement "GiftTastes" project (box farmer) []).IsNone)

// ---- Animation edits ----

let private withClip () =
    let project = starter ()
    let frame x = { ArtFrame.Default with X = x; Y = 0.0; Width = 16.0; Height = 16.0; Ticks = 6.0 }
    let clip = { AnimationClip.Default with Name = "walk"; Loop = true; Frames = [ frame 0.0; frame 16.0 ] }
    let asset = { CustomAsset.Default with Id = "art-1"; Name = "sheet.png"; Type = "art"; DataUrl = "data:image/png;base64,AA=="; Width = Some(32.0); Height = Some(16.0); Animations = Some([ clip ]) }
    project |> apply (UpsertAsset asset)

let private frames (project: GameProject) = (project.CustomAssets |> Seq.find (fun a -> a.Id = "art-1")).Animations.Value.[0].Frames

[<Fact>]
let ``frame durations change one frame or every frame, never below one tick`` () =
    let project = withClip ()
    let one = project |> apply (SetFrameTicks("art-1", "walk", Some 1, 10))
    Assert.Equal<float list>([ 6.0; 10.0 ], frames one |> Seq.map (fun f -> f.Ticks) |> List.ofSeq)
    let all = project |> apply (SetFrameTicks("art-1", "walk", None, 3))
    Assert.Equal<float list>([ 3.0; 3.0 ], frames all |> Seq.map (fun f -> f.Ticks) |> List.ofSeq)
    let floor = project |> apply (SetFrameTicks("art-1", "walk", None, 0))
    Assert.Equal<float list>([ 1.0; 1.0 ], frames floor |> Seq.map (fun f -> f.Ticks) |> List.ofSeq)
    Assert.Same(project, project |> apply (SetFrameTicks("art-1", "walk", None, 6)))
    Assert.Same(project, project |> apply (SetFrameTicks("art-1", "idle", None, 2)))
    let doc = Document.create project |> Document.apply (SetFrameTicks("art-1", "walk", None, 9)) |> Document.undo
    Assert.Same(project, doc.Project)

[<Fact>]
let ``duplicating a frame inserts the copy after it`` () =
    let project = withClip ()
    let copied = project |> apply (DuplicateFrame("art-1", "walk", 0))
    Assert.Equal<float list>([ 0.0; 0.0; 16.0 ], frames copied |> Seq.map (fun f -> f.X) |> List.ofSeq)
    Assert.Same(project, project |> apply (DuplicateFrame("art-1", "walk", 5)))
    Assert.Same(project, project |> apply (DuplicateFrame("art-2", "walk", 0)))

// ---- Export settings form ----

[<Fact>]
let ``export icon options list large PNG art only`` () =
    let project = starter ()
    let asset id size = { CustomAsset.Default with Id = id; Name = id; Type = "art"; DataUrl = "data:image/png;base64,AA=="; Width = Some(size); Height = Some(size) }
    let withArt = project |> apply (UpsertAsset(asset "big" 256.0)) |> apply (UpsertAsset(asset "small" 64.0))
    let options = ExportSettingsForm.IconOptions(withArt, null) |> List.ofSeq
    Assert.Equal<string list>([ ""; "big" ], ids options)
    let missing = ExportSettingsForm.IconOptions(withArt, "small") |> List.ofSeq
    Assert.Equal<string list>([ ""; "small"; "big" ], ids missing)
    Assert.True missing.[1].Missing
    Assert.Equal<string list>([ "integer"; "fit" ], ExportSettingsForm.PixelScales |> Seq.map (fun o -> o.Id) |> List.ofSeq)
    let settings = ExportSettingsForm.Current withArt
    Assert.Empty(ExportSettingsForm.Check(withArt, settings))
    let bad = { settings with Window = { ExportWindow.Default with Width = 100; Height = 100 }; IconAssetId = Some "small" }
    let codes = ExportSettingsForm.Check(withArt, bad) |> Seq.map (fun p -> p.Code) |> List.ofSeq
    Assert.Equal<string list>([ "export.window"; "export.icon" ], codes)
