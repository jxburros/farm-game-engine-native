namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// What a form field holds, so the editor can pick a control.
[<RequireQualifiedAccess>]
type FieldKind =
    | Text
    | Integer
    | Number
    /// A switch; the labels say what on and off mean ("is set" / "is NOT set").
    | Bool of onLabel: string * offLabel: string
    | OneOf of choices: (string * string) list
    | Reference of kind: ReferenceKind
    | ReferenceList of kind: ReferenceKind

/// One field of a condition or outcome form (event-forms.tsx). `Key` is the JSON key. Clearing
/// an `Optional` field removes it; clearing a required number stores `WhenEmpty` (the web
/// `n ?? 0` / `n ?? 1`). `Min`/`Max` clamp numbers like the web does.
type VocabularyField =
    { Key: string
      Label: string
      Kind: FieldKind
      Optional: bool
      Placeholder: string
      WhenEmpty: float
      Min: float option
      Max: float option }

/// Port of `event-vocabulary.ts` and the per-type fields of `event-forms.tsx`: the condition and
/// outcome types events, actions and minigame tiers share, which fields each type shows, and the
/// defaults a new or re-typed row starts from.
module Vocabulary =
    let private field key label kind =
        { Key = key; Label = label; Kind = kind; Optional = false; Placeholder = ""; WhenEmpty = 0.0; Min = None; Max = None }

    let private integer key label = field key label FieldKind.Integer
    let private optionalInteger key label = { integer key label with Optional = true }
    let private text key label placeholder = { field key label FieldKind.Text with Placeholder = placeholder }
    let private reference key label kind = field key label (FieldKind.Reference kind)

    /// `CONDITION_TYPES`, in the web order.
    let conditionTypes =
        [ "enterTile", "Enter tile/region"
          "interactTile", "Interact with tile"
          "friendship", "Friendship threshold"
          "weather", "Weather"
          "inventorySpace", "Inventory can receive item"
          "hasItem", "Has item ≥ n"
          "flag", "Flag is set/unset"
          "dayRange", "Day range"
          "season", "Season"
          "yearRange", "Year range"
          "timeOfDay", "Time of day"
          "questStatus", "Quest status"
          "festivalId", "Festival" ]

    /// `OUTCOME_TYPES`, in the web order (the legacy `unlockScene` is not offered).
    let outcomeTypes =
        [ EventOutcomeTypes.ModifyFriendship, "Change friendship"
          EventOutcomeTypes.ModifyEnergy, "Restore / spend energy"
          EventOutcomeTypes.WaterArea, "Water nearby soil (magic)"
          EventOutcomeTypes.Message, "Show message"
          EventOutcomeTypes.GiveItem, "Give item"
          EventOutcomeTypes.TakeItem, "Take item"
          EventOutcomeTypes.GiveMoney, "Give money"
          EventOutcomeTypes.TakeMoney, "Take money"
          EventOutcomeTypes.SetFlag, "Set flag"
          EventOutcomeTypes.ClearFlag, "Clear flag"
          EventOutcomeTypes.StartQuest, "Start quest"
          EventOutcomeTypes.CompleteQuest, "Complete quest"
          EventOutcomeTypes.SpawnNpc, "Spawn NPC"
          EventOutcomeTypes.RemoveNpc, "Remove NPC"
          EventOutcomeTypes.ChangeTile, "Change tile"
          EventOutcomeTypes.WarpPlayer, "Warp player"
          EventOutcomeTypes.StartDialogue, "Start dialogue"
          EventOutcomeTypes.LockTransition, "Lock transition"
          EventOutcomeTypes.UnlockTransition, "Unlock transition"
          EventOutcomeTypes.PlaySound, "Play sound"
          EventOutcomeTypes.PerformAction, "Perform action"
          EventOutcomeTypes.StartMinigame, "Start minigame" ]

    /// The label of a condition type (the type itself when unknown).
    let conditionLabel (kind: string) =
        conditionTypes |> List.tryFind (fun (value, _) -> value = kind) |> Option.map snd |> Option.defaultValue kind

    /// The label of an outcome type (the type itself when unknown, like the web).
    let outcomeLabel (kind: string) =
        outcomeTypes |> List.tryFind (fun (value, _) -> value = kind) |> Option.map snd |> Option.defaultValue kind

    /// `ConditionRow`: the fields each condition type shows.
    let conditionFields (kind: string) : VocabularyField list =
        match kind with
        | "enterTile"
        | "interactTile" -> [ integer "x" "X"; integer "y" "Y"; optionalInteger "x2" "X2 (opt)"; optionalInteger "y2" "Y2 (opt)" ]
        | "friendship" -> [ reference "npcId" "Character" ReferenceKind.Npc; integer "min" "Minimum friendship" ]
        | "weather" -> [ field "weatherIds" "Weather IDs" (FieldKind.ReferenceList ReferenceKind.Weather) ]
        | "hasItem"
        | "inventorySpace" -> [ reference "itemId" "Item" ReferenceKind.Item; { integer "quantity" "Quantity ≥" with WhenEmpty = 1.0 } ]
        | "flag" -> [ text "flag" "Flag name" ""; field "value" "Flag state" (FieldKind.Bool("is set", "is NOT set")) ]
        | "dayRange" -> [ optionalInteger "minDay" "From day"; optionalInteger "maxDay" "To day" ]
        | "yearRange" -> [ optionalInteger "minYear" "From year"; optionalInteger "maxYear" "To year" ]
        | "timeOfDay" -> [ integer "minMinute" "From minute"; integer "maxMinute" "To minute" ]
        | "season" -> [ field "seasons" "Seasons" (FieldKind.ReferenceList ReferenceKind.Season) ]
        | "festivalId" -> [ reference "festivalId" "Festival" ReferenceKind.Festival ]
        | "questStatus" ->
            [ reference "questId" "Quest" ReferenceKind.Quest
              field "status" "Status" (FieldKind.OneOf(QuestStatuses.All |> Seq.map (fun s -> s, s) |> List.ofSeq)) ]
        | _ -> []

    /// `OutcomeFields`: the fields each outcome type shows.
    let outcomeFields (kind: string) : VocabularyField list =
        let item = reference "itemId" "Item" ReferenceKind.Item
        let npc = reference "npcId" "NPC" ReferenceKind.Npc
        let scene = reference "sceneId" "Scene" ReferenceKind.Scene
        match kind with
        | EventOutcomeTypes.Message -> [ text "message" "Message" "Message text…" ]
        | EventOutcomeTypes.GiveItem
        | EventOutcomeTypes.TakeItem -> [ item; { integer "itemQuantity" "Quantity" with WhenEmpty = 1.0 } ]
        | EventOutcomeTypes.GiveMoney
        | EventOutcomeTypes.TakeMoney -> [ integer "amount" "Amount" ]
        | EventOutcomeTypes.ModifyEnergy -> [ integer "amount" "Energy change" ]
        | EventOutcomeTypes.WaterArea -> [ { integer "radius" "Radius (0-10 tiles)" with WhenEmpty = 1.0; Min = Some 0.0; Max = Some 10.0 } ]
        | EventOutcomeTypes.ModifyFriendship -> [ npc; integer "amount" "Friendship change" ]
        | EventOutcomeTypes.SetFlag
        | EventOutcomeTypes.ClearFlag -> [ text "flagName" "Flag name" "flag-name" ]
        | EventOutcomeTypes.StartQuest
        | EventOutcomeTypes.CompleteQuest -> [ reference "questId" "Quest" ReferenceKind.Quest ]
        | EventOutcomeTypes.SpawnNpc -> [ npc; optionalInteger "x" "X (opt)"; optionalInteger "y" "Y (opt)" ]
        | EventOutcomeTypes.RemoveNpc
        | EventOutcomeTypes.StartDialogue -> [ npc ]
        | EventOutcomeTypes.ChangeTile ->
            [ integer "tileX" "X"; integer "tileY" "Y"
              field "newTileType" "New type" (FieldKind.OneOf(TileTypes.All |> Seq.map (fun t -> t, t) |> List.ofSeq)) ]
        | EventOutcomeTypes.WarpPlayer -> [ scene; integer "x" "X"; integer "y" "Y" ]
        | EventOutcomeTypes.LockTransition
        | EventOutcomeTypes.UnlockTransition -> [ scene; integer "x" "From X"; integer "y" "From Y" ]
        | EventOutcomeTypes.PlaySound -> [ text "soundId" "Sound" "sound-id (audio lands in M7)" ]
        | EventOutcomeTypes.PerformAction -> [ reference "actionId" "Action" ReferenceKind.Action ]
        | EventOutcomeTypes.StartMinigame -> [ reference "minigameId" "Minigame" ReferenceKind.Minigame ]
        | _ -> []

    /// `defaultCondition`: a condition of the type with the web's starting values.
    let defaultCondition (kind: string) (project: GameProject) : EventCondition = Defaults.defaultCondition kind project

    /// The web adds `{ type }`: an outcome of the type with nothing filled in yet.
    let defaultOutcome (kind: string) : EventOutcome = Defaults.defaultOutcome kind

    /// QuestEditor: the target fields each objective type shows. Other target fields stay hidden.
    let objectiveTargets (kind: string) : string list =
        match kind with
        | QuestObjectiveTypes.Collect
        | QuestObjectiveTypes.Craft
        | QuestObjectiveTypes.Gift -> [ "TargetItemId"; "TargetItemQuantity" ]
        | QuestObjectiveTypes.Harvest -> [ "TargetCropType"; "TargetCropQuantity" ]
        | QuestObjectiveTypes.Talk -> [ "TargetNpcId" ]
        | QuestObjectiveTypes.Visit -> [ "TargetSceneId" ]
        | _ -> []

    let private objectiveTargetProperties =
        [ "TargetItemId"; "TargetItemQuantity"; "TargetCropType"; "TargetCropQuantity"; "TargetNpcId"; "TargetSceneId" ]

    /// Properties of `owner` a form hides while its `type` discriminator is `kind`.
    let hiddenProperties (owner: string) (kind: string) : string list =
        match owner with
        | "QuestObjective" ->
            let shown = objectiveTargets kind
            objectiveTargetProperties |> List.filter (fun p -> not (List.contains p shown))
        | _ -> []

    let private some (value: 'T) : obj option = Some(box value |> nonNull)

    let private firstId (options: PickerOption list) =
        match options with
        | first :: _ -> first.Id
        | [] -> ""

    /// A new element for a list in a content form (the web "Add" buttons). `entity` is the entry
    /// being edited and `siblingIds` the ids already in the list (objectives need unique ids).
    /// `None` when the type has no special default; the form then starts from the record's own
    /// defaults.
    let newElement (elementType: string) (project: GameProject) (entity: obj) (siblingIds: string list) : obj option =
        let firstItem () = firstId (References.options ReferenceKind.Item project)
        match elementType with
        | "ShopStockEntry" -> some { ShopStockEntry.Default with ItemId = firstItem () }
        | "RecipeIngredient" -> some ({ ItemId = firstItem (); Quantity = 1.0 } : RecipeIngredient)
        | "NodeDrop" -> some ({ ItemId = firstItem (); Min = 1.0; Max = 1.0; Weight = 1.0; Extra = [] } : NodeDrop)
        | "FishTableEntry" -> some ({ ItemId = firstItem (); Weight = 1.0; Difficulty = 0.3 } : FishTableEntry)
        | "QuestRewardItem" -> some ({ ItemId = firstItem (); Quantity = 1.0 } : QuestRewardItem)
        | "MineRockWeight" -> some ({ NodeTypeId = firstId (References.options ReferenceKind.NodeType project); Weight = 1.0 } : MineRockWeight)
        | "WeatherTableEntry" -> some ({ WeatherId = firstId (References.options ReferenceKind.Weather project); Weight = 1.0 } : WeatherTableEntry)
        | "QuestObjective" ->
            some
                { QuestObjective.Default with
                    Id = Defaults.nextId "obj" siblingIds; Type = QuestObjectiveTypes.Collect
                    Description = "New objective"; Completed = false; Progress = 0.0 }
        | "DialogueOption" -> some (Defaults.newDialogueOption ())
        | "MinigameResultTier" -> some (Defaults.newResultTier ())
        | "NpcScheduleEntry" ->
            match entity with
            | :? Npc as npc -> some (Defaults.newScheduleEntry npc)
            | _ -> None
        | "GridPoint" ->
            match entity with
            | :? Npc as npc -> some ({ X = npc.X; Y = npc.Y } : GridPoint)
            | _ -> None
        | "Dialogue" ->
            match entity with
            | :? Npc as npc ->
                let taken = Seq.append (Defaults.allIds project) (npc.Dialogue |> Seq.map (fun d -> d.Id)) |> Seq.append siblingIds
                let dialogue = Defaults.newDialogue project npc.Id
                some { dialogue with Id = Defaults.nextId "dialogue" taken }
            | _ -> None
        | "EventCondition" -> some (defaultCondition "enterTile" project)
        | "EventOutcome" -> some (defaultOutcome EventOutcomeTypes.Message)
        | _ -> None
