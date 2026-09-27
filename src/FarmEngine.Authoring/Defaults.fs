namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open System.Text.Json
open FarmEngine.Core
open FarmEngine.Json
open FarmEngine.Schemas

/// What the web editor does when the creator presses "Add" or "New": the same default values
/// as the TSX components, with deterministic ids instead of `Date.now()`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Defaults =
    /// The smallest `prefix-N` (N ≥ 1) not in `existingIds`. Replaces the web's
    /// `` `${prefix}-${Date.now()}` `` so ids never depend on the wall clock.
    let nextId (prefix: string) (existingIds: seq<string>) : string =
        let taken = HashSet<string>(existingIds)
        let rec go n =
            let candidate = sprintf "%s-%d" prefix n
            if taken.Contains candidate then go (n + 1) else candidate
        go 1

    /// ActionsEditor `slugifyId` / ProjectSettingsEditor `slugifySeasonId`: a lowercase
    /// dashed id from a display name, `fallback` when nothing is left, `-2`, `-3`… on collision.
    let slugId (name: string) (existingIds: seq<string>) (fallback: string) : string =
        let taken = HashSet<string>(existingIds)
        let lowered = name.Trim().ToLowerInvariant()
        let builder = Text.StringBuilder()
        let mutable pendingDash = false
        for c in lowered do
            if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' then
                if pendingDash && builder.Length > 0 then builder.Append '-' |> ignore
                pendingDash <- false
                builder.Append c |> ignore
            else
                pendingDash <- true
        let slug = builder.ToString().Trim('-')
        let slug = if slug.Length = 0 then fallback else slug
        if not (taken.Contains slug) then slug
        else
            let rec go n =
                let candidate = sprintf "%s-%d" slug n
                if taken.Contains candidate then go (n + 1) else candidate
            go 2

    /// Create the export identity once when a creator enables desktop export. The persisted
    /// game id uses the project id, so later edits to the display name cannot move saves.
    let newExportSettings (project: GameProject) : ExportSettings =
        ExportSettings(GameId = "local." + slugId project.Id Seq.empty "game",
                       ExecutableName = slugId project.Name Seq.empty "game")

    /// Every id in the project, so a new id collides with nothing (web `creator-patterns.ts` `ids`).
    let allIds (project: GameProject) : seq<string> = Proj.allIds project :> seq<string>

    let private currentScene (project: GameProject) = Proj.currentScene project

    let private firstItemId (project: GameProject) : string =
        match Seq.tryHead project.Items with
        | Some item -> item.Id
        | None -> ""

    let private firstSeasonId (project: GameProject) : string =
        match Seq.tryHead project.Settings.Calendar.Seasons with
        | Some season -> season.Id
        | None -> "spring"

    /// NPCEditor `createNPC`: placed in the middle of the current scene, stationary, no dialogue.
    let newNpcWith (project: GameProject) (name: string) (appearance: string) (canMove: bool) : Npc =
        let scene = currentScene project
        let width, height =
            match scene with
            | Some s -> s.Width, s.Height
            | None -> 16.0, 12.0
        Npc(Id = nextId "npc" (allIds project),
            Name = (if String.IsNullOrWhiteSpace name then "New NPC" else name.Trim()),
            X = Math.Floor(width / 2.0), Y = Math.Floor(height / 2.0),
            SceneId = (match scene with Some s -> s.Id | None -> ""),
            CanMove = canMove, MovePattern = NpcMovePatterns.Stationary, Appearance = appearance)

    /// NPCEditor `createNPC` with the form defaults (villager, cannot move).
    let newNpc (project: GameProject) (name: string) : Npc = newNpcWith project name "villager" false

    /// NPCDetailEditor `createDialogue`: "Hello there!" with a single "Goodbye" option.
    let newDialogue (project: GameProject) (npcId: string) : Dialogue =
        Dialogue(Id = nextId "dialogue" (allIds project), NpcId = npcId, Text = "Hello there!",
                 Options = List<DialogueOption>([ DialogueOption(Text = "Goodbye") ]))

    /// DialogueEditor `addOption`.
    let newDialogueOption () : DialogueOption = DialogueOption(Text = "New option")

    /// NPC MovementScheduleSection "Add" schedule entry: 8:00 at the NPC's home tile.
    let newScheduleEntry (npc: Npc) : NpcScheduleEntry =
        NpcScheduleEntry(Minute = 8.0 * 60.0, SceneId = npc.SceneId, X = npc.X, Y = npc.Y)

    /// ItemEditor form defaults: a stackable gift worth nothing; the id follows `custom-{type}-N`.
    let newItemOfType (project: GameProject) (itemType: string) : Item =
        Item(Id = nextId (sprintf "custom-%s" itemType) (allIds project), Name = "New Item", Description = "",
             Type = itemType, Stackable = true, MaxStack = 99.0, Value = 0.0)

    let newItem (project: GameProject) : Item = newItemOfType project ItemTypes.Gift

    /// CropEditor `handleCreateNew`.
    let newCrop (project: GameProject) : CustomCropDefinition =
        CustomCropDefinition(Id = nextId "custom" (allIds project), Name = "New Crop", SeedCost = 10.0, BaseHarvestValue = 20.0,
                             GrowthTime = 15000.0, GrowthDays = Nullable 3.0, Stages = 4.0,
                             Seasons = List<string>([ firstSeasonId project ]), CanRegrow = false,
                             YieldMin = 1.0, YieldMax = 2.0, MutationChance = Nullable 0.01)

    /// CropEditor `handleDuplicateCrop`: same values, new id, " Copy" name, no custom art.
    let duplicateCrop (project: GameProject) (crop: CustomCropDefinition) : CustomCropDefinition =
        Records.withValues crop [ ("Id", box (nextId "custom" (allIds project))); ("Name", box (sprintf "%s Copy" crop.Name)); ("CustomAsset", null) ]

    /// QuestEditor `handleCreateQuest`.
    let newQuest (project: GameProject) : Quest =
        Quest(Id = nextId "quest" (allIds project), Name = "New Quest", Description = "Quest description",
              Status = QuestStatuses.NotStarted, AutoStart = Nullable false, Repeatable = Nullable false)

    /// QuestEditor `handleAddObjective`: a collect objective with no target yet.
    let newObjective (quest: Quest) : QuestObjective =
        QuestObjective(Id = nextId "obj" (quest.Objectives |> Seq.map (fun o -> o.Id)), Type = QuestObjectiveTypes.Collect,
                       Description = "New objective", Completed = false, Progress = 0.0)

    /// QuestEditor "Add Item Reward": the first item, quantity 1 (none when the project has no items).
    let newRewardItem (project: GameProject) : QuestRewardItem option =
        match Seq.tryHead project.Items with
        | Some item -> Some(QuestRewardItem(ItemId = item.Id, Quantity = 1.0))
        | None -> None

    /// event-vocabulary.ts `defaultCondition`.
    let defaultCondition (kind: string) (project: GameProject) : EventCondition =
        match kind with
        | "enterTile" -> EnterTileCondition(X = 0.0, Y = 0.0) :> EventCondition
        | "interactTile" -> InteractTileCondition(X = 0.0, Y = 0.0) :> EventCondition
        | "inventorySpace" -> InventorySpaceCondition(ItemId = "", Quantity = 1.0) :> EventCondition
        | "hasItem" -> HasItemCondition(ItemId = "", Quantity = 1.0) :> EventCondition
        | "dayRange" -> DayRangeCondition(MinDay = Nullable 1.0) :> EventCondition
        | "season" -> SeasonCondition(Seasons = List<string>([ firstSeasonId project ])) :> EventCondition
        | "yearRange" -> YearRangeCondition(MinYear = Nullable 1.0) :> EventCondition
        | "timeOfDay" -> TimeOfDayCondition(MinMinute = 360.0, MaxMinute = 720.0) :> EventCondition
        | "questStatus" -> QuestStatusCondition(QuestId = "", Status = QuestStatuses.Completed) :> EventCondition
        | "friendship" -> FriendshipCondition(NpcId = "", Min = 250.0) :> EventCondition
        | "weather" -> WeatherCondition(WeatherIds = List<string>([ "rain" ])) :> EventCondition
        | "festivalId" ->
            let festival =
                match Seq.tryHead project.Settings.Calendar.Festivals with
                | Some f -> f.Id
                | None -> ""
            FestivalIdCondition(FestivalId = festival) :> EventCondition
        | _ -> FlagCondition(Flag = "", Value = true) :> EventCondition

    /// event-forms.tsx outcome picker: an outcome of the kind with nothing filled in yet.
    let defaultOutcome (kind: string) : EventOutcome = EventOutcome(Type = kind)

    /// EventsEditor `addEvent`: on the start scene, on enter at (0,0), showing a message.
    let newEvent (project: GameProject) : GameEvent =
        GameEvent(Id = nextId "event" (allIds project), Name = "New Event", SceneId = project.StartSceneId, Trigger = EventTriggers.Enter,
                  Conditions = List<EventCondition>([ EnterTileCondition(X = 0.0, Y = 0.0) :> EventCondition ]),
                  Outcomes = List<EventOutcome>([ EventOutcome(Type = EventOutcomeTypes.Message, Message = "Something happens…") ]),
                  Active = true, Repeatable = false)

    /// ShopEditor `handleAddShop`.
    let newShop (project: GameProject) : ShopDefinition =
        ShopDefinition(Id = nextId "shop" (allIds project), Name = "New Shop", SellPriceMultiplier = 1.0, BuysItems = true,
                       RepairsTools = false, RepairCostPerPoint = 0.5)

    /// ShopEditor `handleAddStockEntry`: the first item (none when the project has no items).
    let newStockEntry (project: GameProject) : ShopStockEntry option =
        match Seq.tryHead project.Items with
        | Some item -> Some(ShopStockEntry(ItemId = item.Id))
        | None -> None

    /// RecipeEditor "Recipe": a hand craft with nothing in or out yet.
    let newRecipe (project: GameProject) : RecipeDefinition =
        RecipeDefinition(Id = nextId "recipe" (allIds project), Name = "New Recipe", ProcessingMinutes = 0.0, Category = "crafting")

    /// RecipeEditor ingredient "+": the first item, quantity 1.
    let newIngredient (project: GameProject) : RecipeIngredient option =
        match Seq.tryHead project.Items with
        | Some item -> Some(RecipeIngredient(ItemId = item.Id, Quantity = 1.0))
        | None -> None

    /// RecipeEditor "Machine".
    let newMachineType (project: GameProject) : MachineTypeDefinition =
        MachineTypeDefinition(Id = nextId "machine-custom" (allIds project), Name = "New Machine", Description = "", Color = "#9a7b4f", BlocksMovement = true)

    /// NodeTypeEditor `addNodeType`: three axe hits, no drops, never respawns.
    let newNodeType (project: GameProject) : NodeTypeDefinition =
        NodeTypeDefinition(Id = nextId "node-custom" (allIds project), Name = "New Node", Health = 3.0, RequiredTool = ToolTypes.Axe,
                           RequiredToolTier = 1.0, RespawnDays = Nullable(), Color = "#7a5a3a", BlocksMovement = true)

    /// NodeTypeEditor drop "+": the first item, 1..1, weight 1.
    let newDrop (project: GameProject) : NodeDrop =
        NodeDrop(ItemId = firstItemId project, Min = 1.0, Max = 1.0, Weight = 1.0)

    /// WildlifeEditor "Species": costs 500, eats hay when the project has it, makes the first item.
    let newAnimalSpecies (project: GameProject) : AnimalSpeciesDefinition =
        let feed: string | null = if project.Items |> Seq.exists (fun i -> i.Id = "feed-hay") then "feed-hay" else null
        AnimalSpeciesDefinition(Id = nextId "species" (allIds project), Name = "New Species", PurchaseCost = 500.0, FeedItemId = feed,
                                ProductItemId = firstItemId project, ProductIntervalDays = 1.0, DaysToAdult = 3.0, Color = "#e8d8c3")

    /// App.tsx animal placement: a newborn of the species, content, unfed, on the clicked tile.
    let newAnimal (project: GameProject) (speciesId: string) (sceneId: string) (x: int) (y: int) : AnimalState =
        let name =
            match project.AnimalSpecies |> Seq.tryFind (fun s -> s.Id = speciesId) with
            | Some species -> species.Name
            | None -> speciesId
        AnimalState(Id = nextId "animal" (allIds project), SpeciesId = speciesId, Name = name, SceneId = sceneId, X = float x, Y = float y,
                    Mood = 70.0, FedToday = false, PettedToday = false, AgeDays = 0.0, DaysSinceProduct = 0.0, ProductReady = false)

    /// WildlifeEditor "Table".
    let newFishTable (project: GameProject) : FishTable =
        FishTable(Id = nextId "fish-table" (allIds project), Name = "New Waters", JunkChance = 0.15)

    /// WildlifeEditor fish entry "+": the first item, weight 1, difficulty 0.3.
    let newFishEntry (project: GameProject) : FishTableEntry option =
        match Seq.tryHead project.Items with
        | Some item -> Some(FishTableEntry(ItemId = item.Id, Weight = 1.0, Difficulty = 0.3))
        | None -> None

    /// ActionsEditor `addAction`: id from the name, one message outcome.
    let newAction (project: GameProject) : ActionDef =
        ActionDef(Id = slugId "New Action" (allIds project) "action", Name = "New Action", Description = "", FailMessage = "",
                  Outcomes = List<EventOutcome>([ EventOutcome(Type = EventOutcomeTypes.Message, Message = "Something happens…") ]), EnergyCost = 0.0)

    /// ActionsEditor `duplicateAction`: "(copy)" with a fresh slug id.
    let duplicateAction (project: GameProject) (action: ActionDef) : ActionDef =
        Records.withValues action
            [ ("Id", box (slugId (sprintf "%s copy" action.Name) (allIds project) "action")); ("Name", box (sprintf "%s (copy)" action.Name)) ]

    /// ActionsEditor `TIMING_BAR_DEFAULTS`.
    let timingBarConfig () : OrderedDictionary<string, JsonElement> =
        let config = OrderedDictionary<string, JsonElement>()
        config["speed"] <- Js.Value 0.9
        config["targetSize"] <- Js.Value 0.18
        config["prompt"] <- Js.Value "Stop the marker in the zone!"
        config

    /// ActionsEditor `addMinigame`: a timing bar with one catch-all tier.
    let newMinigame (project: GameProject) : MinigameDef =
        MinigameDef(Id = slugId "New Minigame" (allIds project) "minigame", Name = "New Minigame", Kind = "timing-bar", Config = timingBarConfig (),
                    ResultTiers = List<MinigameResultTier>([ MinigameResultTier(MinScore = 0.0) ]))

    /// ActionsEditor `duplicateMinigame`.
    let duplicateMinigame (project: GameProject) (minigame: MinigameDef) : MinigameDef =
        Records.withValues minigame
            [ ("Id", box (slugId (sprintf "%s copy" minigame.Name) (allIds project) "minigame")); ("Name", box (sprintf "%s (copy)" minigame.Name)) ]

    /// MinigameEditor `addTier`.
    let newResultTier () : MinigameResultTier = MinigameResultTier(MinScore = 0.0)

    /// SceneManager `createScene`: `createEmptyScene` filled with the chosen tile type (grass by default).
    let newSceneWith (project: GameProject) (name: string) (width: int) (height: int) (defaultTile: string) : Scene =
        let width = max 1 width
        let height = max 1 height
        let name = if String.IsNullOrWhiteSpace name then "New Scene" else name.Trim()
        let scene = Tiles.CreateEmptyScene(nextId "scene" (allIds project), name, float width, float height)
        if defaultTile = TileTypes.Grass then scene
        else Proj.mapTiles (fun tile -> Tiles.CreateEmptyTile(tile.X, tile.Y, defaultTile)) scene

    /// SceneManager `createScene` with the dialog defaults (16×12, grass).
    let newScene (project: GameProject) (name: string) (width: int) (height: int) : Scene = newSceneWith project name width height TileTypes.Grass

    /// TransitionEditor form defaults: from (0,0) to (0,0) of the chosen scene, unlocked.
    let newTransition (toSceneId: string) : SceneTransition =
        SceneTransition(FromX = 0.0, FromY = 0.0, ToSceneId = toSceneId, ToX = 0.0, ToY = 0.0)

    /// TransitionEditor `createBidirectional`: the door plus its return door, as one edit.
    let linkScenes (fromSceneId: string) (transition: SceneTransition) : Edit =
        let back = SceneTransition(FromX = transition.ToX, FromY = transition.ToY, ToSceneId = fromSceneId, ToX = transition.FromX, ToY = transition.FromY)
        Batch("Link scenes", [ SetTransition(fromSceneId, transition); SetTransition(transition.ToSceneId, back) ])

    /// ProjectSettingsEditor `addSeason`: 28 days, id from the name.
    let newSeason (calendar: CalendarConfig) : CalendarSeason =
        CalendarSeason(Id = slugId "New Season" (calendar.Seasons |> Seq.map (fun s -> s.Id)) "season", Name = "New Season", Days = 28.0)

    /// ProjectSettingsEditor `addFestival`: day 1 of the first season (none without seasons).
    let newFestival (project: GameProject) (calendar: CalendarConfig) : CalendarFestival option =
        match Seq.tryHead calendar.Seasons with
        | None -> None
        | Some season -> Some(CalendarFestival(Id = nextId "festival" (allIds project |> Seq.append (calendar.Festivals |> Seq.map (fun f -> f.Id))), Name = "New Festival", SeasonId = season.Id, Day = 1.0))

    /// ProjectSettingsEditor mine toggle: enabling fills in the default bands when there are none.
    let mineEnabled (project: GameProject) (enabled: bool) : MineConfig =
        let mine = project.Mine
        let bands = if mine.Bands.Count > 0 then mine.Bands else ContentBuiltin.CreateDefaultMineBands()
        Records.withValues mine [ ("Enabled", box enabled); ("Bands", box bands) ]

    /// InterfaceEditor: an empty creator panel.
    let newGamePanel (project: GameProject) : GamePanel =
        let existing =
            match project.GamePanels with
            | null -> Seq.empty
            | panels -> panels |> Seq.map (fun p -> p.Id)
        GamePanel(Id = nextId "panel" existing, Title = "New Panel")
