namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// A New Project choice; the desktop host only displays these authored labels.
type ProjectTemplateInfo = { Id: string; Name: string; Description: string }

/// Sample transformations from src/lib/templates.ts, with time supplied by the host.
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
        { project with
            Name = "Cozy Garden"
            Settings = settings
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
                Id = "npc-elder"; Name = "Elder Rowan"; X = 2.0; Y = 3.0; SceneId = "scene-farm"
                Dialogue = [ dialogue ]; CanMove = false; MovePattern = Some "stationary"; Appearance = "farmer" }
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
        { project with
            Name = "Quest RPG"
            Npcs = project.Npcs @ [ elder ]
            Dialogues = project.Dialogues @ [ dialogue ]
            Quests = project.Quests @ quests }

/// Project creation boundary for C#. Time is explicit: reading the OS clock belongs to the
/// desktop host, so sample generation and replay tests remain deterministic.
[<AbstractClass; Sealed>]
type ProjectCatalog =
    static member TemplateInfo : IReadOnlyList<ProjectTemplateInfo> =
        [| { Id = "starter"; Name = "Starter Farm"; Description = "The full farming loop: crops, shop, quests, crafting." }
           { Id = "cozy"; Name = "Cozy Garden"; Description = "Pure-farm relaxation — no energy, no collapse, slow days." }
           { Id = "quest"; Name = "Quest RPG"; Description = "Story-driven: a quest chain, gated dialogue and an elder NPC." }
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
