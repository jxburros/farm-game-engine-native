namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// A New Project choice; the desktop host only displays these authored labels.
type ProjectTemplateInfo = { Id: string; Name: string; Description: string }

/// Maps drawn as text, one character per tile, for the samples' own scenes.
module private SampleMaps =
    /// A scene painted from `rows`: '.' grass, '=' path, '~' water, 's' soil, 'f' floor,
    /// '#' wall, 'D' door, 'T' tree, 'R' rock, 'w' weeds.
    let scene (id: string) (name: string) (rows: string list) : Scene =
        let width = rows.Head.Length
        if rows |> List.exists (fun row -> row.Length <> width) then invalidArg (nameof rows) "rows differ in length"
        let blank = AuthoringTiles.CreateEmptyScene(id, name, float width, float rows.Length)
        let tile (row: string) (tile: Tile) =
            let paint kind = AuthoringTiles.SetTileLayer(tile, kind)
            let node kind health = { tile with Node = Some { TypeId = kind; RemainingHealth = health; DepletedOnDay = None; Extra = [] } }
            match row.[int tile.X] with
            | '.' -> tile
            | '=' -> paint "path"
            | '~' -> paint "water"
            | 's' -> paint "soil"
            | 'f' -> paint "floor"
            | '#' -> paint "wall"
            | 'D' -> paint "door"
            | 'T' -> node "node-tree" 4.0
            | 'R' -> node "node-rock" 3.0
            | 'w' -> node "node-weeds" 1.0
            | other -> invalidArg (nameof rows) (sprintf "unknown map character '%c'" other)
        { blank with Tiles = List.map2 (fun row tiles -> List.map (tile row) tiles) rows blank.Tiles }

    /// Crops already growing: (x, y, crop type, watered days grown, stage).
    let plant (crops: (int * int * string * float * float) list) (scene: Scene) : Scene =
        let crop kind grown stage : Crop =
            { Crop.Default with
                Type = kind; PlantedOnDay = Some 1.0; DaysGrown = Some grown; Stage = stage; Quality = CropQualities.Normal }
        let at x y = crops |> List.tryFind (fun (cx, cy, _, _, _) -> cx = x && cy = y)
        { scene with
            Tiles =
                scene.Tiles
                |> List.map (List.map (fun tile ->
                    match at (int tile.X) (int tile.Y) with
                    | Some(_, _, kind, grown, stage) -> { tile with Crop = Some(crop kind grown stage) }
                    | None -> tile)) }

    /// The Cozy Garden's farm: a hedge of trees, a pond, flower and vegetable beds (some already
    /// growing, one ready to pick), the merchant's stall and a chicken run.
    let garden =
        [ "TTTTTTTTTTTTTTTT"
          "T~~~......w....T"
          "T~~~.ss.ss.fff.T"
          "T~~..ss.ss.f.f.T"
          "T....========..T"
          "Tw...=......=..T"
          "T....=.ssss.=.wT"
          "T....=.ssss.=..T"
          "T.ff.=......=..T"
          "T.ff.====...=..T"
          "T.......=....RRT"
          "TTTTTTTTDTTTTTTT" ]

    /// The Quest RPG's village square, south of the farm: houses, a fountain on a paved plaza,
    /// and the gate back to the farm.
    let square =
        [ "########D#######"
          "#T.....=......T#"
          "#..###.=.###...#"
          "#..###.=.###...#"
          "#......=.......#"
          "#..fffffffff...#"
          "#..ff~~~~~ff...#"
          "#..ff~~~~~ff...#"
          "#..fffffffff...#"
          "#T...........T.#"
          "#TT..R.....w.TT#"
          "################" ]

/// Sample transformations from src/lib/templates.ts, with time supplied by the host. Each sample
/// has its own maps and set dressing, so the New Project choices look and play differently.
module SampleProjects =
    let cozy now : GameProject =
        let project = StarterContent.initial now
        let settings =
            { project.Settings with
                EnergyEnabled = false
                CollapseMoneyPenalty = 0.0
                Time = { project.Settings.Time with MinutesPerRealSecond = 0.5 } }
        let inventory =
            project.Player.Inventory
            |> List.map (fun slot -> if slot.Item.Type = "seed" then { slot with Quantity = slot.Quantity + 10.0 } else slot)
        let garden =
            SampleMaps.scene "scene-farm" "Cottage Garden" SampleMaps.garden
            |> SampleMaps.plant
                [ for x, y in [ 5, 2; 6, 2; 5, 3; 6, 3 ] -> x, y, "strawberry", 2.0, 2.0
                  for x, y in [ 8, 2; 9, 2; 8, 3; 9, 3 ] -> x, y, "carrot", 2.0, 3.0
                  for x in 7 .. 10 -> x, 6, "wheat", 1.0, 1.0 ]
        let hen id name x y : AnimalState =
            { Id = id; SpeciesId = "animal-chicken"; Name = name; SceneId = "scene-farm"; X = x; Y = y; Mood = 70.0
              FedToday = false; PettedToday = false; AgeDays = 30.0; DaysSinceProduct = 0.0; ProductReady = false; Extra = [] }
        { project with
            Name = "Cozy Garden"
            Settings = settings
            Scenes = project.Scenes |> List.map (fun scene -> if scene.Id = "scene-farm" then garden else scene)
            Animals = project.Animals @ [ hen "hen-clover" "Clover" 2.0 8.0; hen "hen-pip" "Pip" 2.0 9.0 ]
            Player = { project.Player with Money = 250.0; Inventory = inventory } }

    let quest now : GameProject =
        let project = StarterContent.initial now
        let dialogue : Dialogue =
            { Id = "dialogue-elder-intro"; NpcId = "npc-elder"
              Text = "Our village once glowed with festival lanterns. Bring me wood and stone, and we will rebuild the square."
              Options =
                [ { DialogueOption.Default with Text = "I will help."; OfferQuestId = Some "quest-rebuild-square" }
                  { DialogueOption.Default with Text = "Maybe later." } ]
              Extra = [] }
        let elder : Npc =
            { Npc.Default with
                Id = "npc-elder"; Name = "Elder Rowan"; X = 8.0; Y = 4.0; SceneId = "scene-square"
                Dialogue = [ dialogue ]; CanMove = false; MovePattern = Some "stationary"; Appearance = "elder" }
        let lanterns : Dialogue =
            { Id = "dialogue-keeper-lanterns"; NpcId = "npc-keeper"
              Text = "When the square is rebuilt, I will hang the lanterns again. Grow wheat for the feast, and the whole village will come."
              Options = [ { DialogueOption.Default with Text = "I'll do my part." } ]
              Extra = [] }
        let keeper : Npc =
            { Npc.Default with
                Id = "npc-keeper"; Name = "Lantern Keeper Ivy"; X = 12.0; Y = 7.0; SceneId = "scene-square"
                Dialogue = [ lanterns ]; CanMove = false; MovePattern = Some "stationary"; Appearance = "villager" }
        let gate : SceneTransition = { SceneTransition.Default with FromX = 8.0; FromY = 11.0; ToSceneId = "scene-square"; ToX = 8.0; ToY = 1.0 }
        let home : SceneTransition = { SceneTransition.Default with FromX = 8.0; FromY = 0.0; ToSceneId = "scene-farm"; ToX = 8.0; ToY = 10.0 }
        let square = { SampleMaps.scene "scene-square" "Village Square" SampleMaps.square with Transitions = [ home ] }
        // The reference sample writes targetQuantity as an extension field, not
        // targetItemQuantity. Preserve its JSON shape during the compatibility phase.
        let collect id description item (quantity: float) : QuestObjective =
            { QuestObjective.Default with
                Id = id; Type = "collect"; Description = description; TargetItemId = Some item
                Extra = [ "targetQuantity", JNumber quantity ]; Completed = false; Progress = 0.0 }
        let quests : Quest list =
            [ { Quest.Default with
                  Id = "quest-rebuild-square"; Name = "Rebuild the Square"
                  Description = "Gather 5 wood and 3 stone for Elder Rowan."; Giver = Some "npc-elder"; Status = "not-started"
                  Objectives = [ collect "obj-wood" "Collect 5 wood" "material-wood" 5.0
                                 collect "obj-stone" "Collect 3 stone" "material-stone" 3.0 ]
                  Rewards = { QuestRewards.Default with Money = Some 200.0 }; AutoStart = Some false; Repeatable = Some false }
              { Quest.Default with
                  Id = "quest-festival-feast"; Name = "Festival Feast"
                  Description = "Grow the harvest for the festival: 5 wheat."; Giver = Some "npc-elder"; Status = "not-started"
                  Objectives =
                    [ { QuestObjective.Default with
                          Id = "obj-feast-wheat"; Type = "harvest"; Description = "Harvest 5 wheat"
                          TargetCropType = Some "wheat"; TargetCropQuantity = Some 5.0; Completed = false; Progress = 0.0 } ]
                  Rewards = { QuestRewards.Default with Money = Some 300.0; Items = Some [ { ItemId = "gift-flower"; Quantity = 3.0 } ] }
                  Prerequisites = Some [ "quest-rebuild-square" ]; AutoStart = Some true; Repeatable = Some false } ]
        let scenes =
            project.Scenes
            |> List.map (fun scene -> if scene.Id = "scene-farm" then { scene with Transitions = scene.Transitions @ [ gate ] } else scene)
        { project with
            Name = "Quest RPG"
            Scenes = scenes @ [ square ]
            Npcs = project.Npcs @ [ elder; keeper ]
            Dialogues = project.Dialogues @ [ dialogue; lanterns ]
            Quests = project.Quests @ quests }

/// Project creation boundary for C#. Time is explicit: reading the OS clock belongs to the
/// desktop host, so sample generation and replay tests remain deterministic.
[<AbstractClass; Sealed>]
type ProjectCatalog =
    static member TemplateInfo : IReadOnlyList<ProjectTemplateInfo> =
        [| { Id = "starter"; Name = "Starter Farm"; Description = "The full farming loop: crops, shop, quests, crafting." }
           { Id = "cozy"; Name = "Cozy Garden"; Description = "A cottage garden with a pond, growing beds and hens — no energy, no collapse, slow days." }
           { Id = "quest"; Name = "Quest RPG"; Description = "Story-driven: a quest chain, gated dialogue and an elder in the village square." }
           { Id = "blank"; Name = "Blank"; Description = "An empty scene and the default catalog. Build from scratch." } |]

    static member All : IReadOnlyList<string> = [| "starter"; "blank"; "cozy"; "quest" |]
    static member SampleIds : IReadOnlyList<string> = [| "starter"; "cozy"; "quest" |]
    static member CreateContentDefaultPack() = StarterContent.pack ()
    static member CreateDefaultPlayer(sceneId: string) = StarterContent.player sceneId
    static member CreateInitialProject(now: float) = StarterContent.initial now
    static member CreateBlankProject(now: float) = StarterContent.blank now
    static member CreateCozyFarmProject(now: float) = SampleProjects.cozy now
    static member CreateQuestRpgProject(now: float) = SampleProjects.quest now

    static member CreateProjectForTemplate(template: string, now: float) =
        match template with
        | "cozy" -> SampleProjects.cozy now
        | "quest" -> SampleProjects.quest now
        | "starter" -> StarterContent.initial now
        | _ -> StarterContent.blank now

    static member CreateSampleProject(sampleId: string, now: float) =
        match sampleId with
        | "cozy" -> SampleProjects.cozy now
        | "quest" -> SampleProjects.quest now
        | _ -> StarterContent.initial now

    /// Date.now().toString(36), for integral millisecond timestamps supplied by the host.
    static member NewProjectId(now: float) =
        let value = int64 now
        let rec digits (n: uint64) acc =
            if n = 0UL then acc else digits (n / 36UL) (string ("0123456789abcdefghijklmnopqrstuvwxyz".[int (n % 36UL)]) + acc)
        // Avoid negating Int64.MinValue in a signed integer.
        let magnitude = if value < 0L then uint64 (-(value + 1L)) + 1UL else uint64 value
        "proj-" + (if value < 0L then "-" else "") + (if magnitude = 0UL then "0" else digits magnitude "")

    static member CreateNewProject(template: string, name: string, id: string | null, now: float) =
        let project = ProjectCatalog.CreateProjectForTemplate(template, now)
        let id = match id with null -> ProjectCatalog.NewProjectId now | value -> value
        { project with Id = id; Name = name }
