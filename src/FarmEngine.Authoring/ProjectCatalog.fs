namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open FarmEngine.Json
open FarmEngine.Schemas

/// A New Project choice; the desktop host only displays these authored labels.
type ProjectTemplateInfo = { Id: string; Name: string; Description: string }

/// Sample transformations from src/lib/templates.ts, with time supplied by the host.
module SampleProjects =
    let private list (xs: seq<'T>) = List<'T>(xs)

    let cozy now =
        let project = StarterContent.initial now
        let time = Records.withValue project.Settings.Time "MinutesPerRealSecond" (box 0.5)
        let settings = Records.withValues project.Settings ["EnergyEnabled", box false; "CollapseMoneyPenalty", box 0.0; "Time", box time]
        let inventory = project.Player.Inventory |> Seq.map (fun slot ->
            if slot.Item.Type = "seed" then Records.withValue slot "Quantity" (box (slot.Quantity + 10.0)) else slot) |> list
        let player = Records.withValues project.Player ["Money", box 250.0; "Inventory", box inventory]
        Records.withValues project ["Name", box "Cozy Garden"; "Settings", box settings; "Player", box player]

    let quest now =
        let project = StarterContent.initial now
        let dialogue = Dialogue(Id = "dialogue-elder-intro", NpcId = "npc-elder",
            Text = "Our village once glowed with festival lanterns. Bring me wood and stone, and we will rebuild the square.",
            Options = list [DialogueOption(Text = "I will help.", OfferQuestId = "quest-rebuild-square")
                            DialogueOption(Text = "Maybe later.")])
        let elder = Npc(Id = "npc-elder", Name = "Elder Rowan", X = 2.0, Y = 3.0, SceneId = "scene-farm",
                        Dialogue = list [dialogue], CanMove = false, MovePattern = "stationary", Appearance = "farmer")
        // The reference sample writes targetQuantity as an extension field, not
        // targetItemQuantity. Preserve its JSON shape during the compatibility phase.
        let collect id description item quantity =
            let extra = Dictionary<string, System.Text.Json.JsonElement>()
            extra["targetQuantity"] <- Js.Value(quantity: float)
            QuestObjective(Id = id, Type = "collect", Description = description, TargetItemId = item,
                           Extra = extra, Completed = false, Progress = 0.0)
        let quests =
            [Quest(Id = "quest-rebuild-square", Name = "Rebuild the Square",
                   Description = "Gather 5 wood and 3 stone for Elder Rowan.", Giver = "npc-elder", Status = "not-started",
                   Objectives = list [collect "obj-wood" "Collect 5 wood" "material-wood" 5.0
                                      collect "obj-stone" "Collect 3 stone" "material-stone" 3.0],
                   Rewards = QuestRewards(Money = Nullable 200.0), AutoStart = false, Repeatable = false)
             Quest(Id = "quest-festival-feast", Name = "Festival Feast",
                   Description = "Grow the harvest for the festival: 5 wheat.", Giver = "npc-elder", Status = "not-started",
                   Objectives = list [QuestObjective(Id = "obj-feast-wheat", Type = "harvest", Description = "Harvest 5 wheat",
                       TargetCropType = "wheat", TargetCropQuantity = Nullable 5.0, Completed = false, Progress = 0.0)],
                   Rewards = QuestRewards(Money = Nullable 300.0, Items = list [QuestRewardItem(ItemId = "gift-flower", Quantity = 3.0)]),
                   Prerequisites = list ["quest-rebuild-square"], AutoStart = true, Repeatable = false)]
        Records.withValues project
            ["Name", box "Quest RPG"; "Npcs", box (list (Seq.append project.Npcs [elder]))
             "Dialogues", box (list (Seq.append project.Dialogues [dialogue]))
             "Quests", box (list (Seq.append project.Quests quests))]

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

    static member CreateProjectFromTemplate(template: string, now: float) : GameProject | null =
        match template with "cozy" -> SampleProjects.cozy now | "quest" -> SampleProjects.quest now | _ -> null

    static member CreateProjectForTemplate(template: string, now: float) =
        match ProjectCatalog.CreateProjectFromTemplate(template, now) with
        | null -> if template = "starter" then StarterContent.initial now else StarterContent.blank now
        | project -> project

    static member CreateSampleProject(sampleId: string | null, now: float) =
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
        Records.withValues project ["Id", box id; "Name", box name]
