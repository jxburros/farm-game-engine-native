namespace FarmEngine.Authoring

open System
open System.Collections.Generic
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
        let builder = System.Text.StringBuilder()
        let mutable pendingDash = false
        for c in lowered do
            if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' then
                if pendingDash && builder.Length > 0 then builder.Append '-' |> ignore
                pendingDash <- false
                builder.Append c |> ignore
            else
                pendingDash <- true
        let slug = builder.ToString().Trim([| '-' |])
        let slug = if slug.Length = 0 then fallback else slug
        if not (taken.Contains slug) then slug
        else
            let rec go n =
                let candidate = sprintf "%s-%d" slug n
                if taken.Contains candidate then go (n + 1) else candidate
            go 2

    /// The longest executable name export accepts. Archive and folder names repeat it
    /// (`X/X.desktop` in a ustar tar holds at most 100 bytes per name).
    [<Literal>]
    let MaxExecutableNameLength = 64

    /// Names an exported game's executable cannot have, compared case-insensitively: Windows
    /// device names, and `licenses` (the folder next to every exported game's executable).
    let private reservedExecutableNames =
        [ "CON"; "PRN"; "AUX"; "NUL"; "LICENSES" ]
        @ [ for prefix in [ "COM"; "LPT" ] do
                for n in 1..9 -> prefix + string n ]
        |> Set.ofList

    /// An executable name export can use: 1 to 64 letters, digits, hyphens or underscores,
    /// starting with a letter or digit, and not a reserved name (a device name or `licenses`).
    let executableNameAllowed (name: string) : bool =
        let allowed c = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-' || c = '_'
        let first c = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        not (String.IsNullOrEmpty name)
        && name.Length <= MaxExecutableNameLength
        && first name.[0]
        && (name |> Seq.forall allowed)
        && not (reservedExecutableNames.Contains(name.ToUpperInvariant()))

    /// The executable name a title becomes when the creator sets none: its slug cut to
    /// `MaxExecutableNameLength`, with `-game` added to a reserved name ("Con" → `con-game`).
    /// Always passes `executableNameAllowed`.
    let defaultExecutableName (title: string) : string =
        let slug = slugId title Seq.empty "game"
        let slug = if slug.Length > MaxExecutableNameLength then slug.Substring(0, MaxExecutableNameLength).TrimEnd('-') else slug
        if executableNameAllowed slug then slug else slug + "-game"

    /// Create the export identity once when a creator enables desktop export. The persisted
    /// game id uses the project id, so later edits to the display name cannot move saves.
    let newExportSettings (project: GameProject) : ExportSettings =
        { ExportSettings.Default with
            GameId = "local." + slugId project.Id Seq.empty "game"
            ExecutableName = Some(defaultExecutableName project.Name) }

    /// ProjectManager "Duplicate": the name the copy gets.
    let copyName (name: string) : string =
        let name = if String.IsNullOrWhiteSpace name then "Untitled Game" else name.Trim()
        sprintf "%s (copy)" name

    /// ProjectManager `duplicateProject`: the same project under a new id and name. A copy is a
    /// separate game, so export settings get a game id of their own (a shared one would share
    /// save folders); the rest of the export identity is kept.
    let duplicateProject (project: GameProject) (newId: string) (name: string) : GameProject =
        let export = project.Export |> Option.map (fun settings -> { settings with GameId = "local." + slugId newId Seq.empty "game" })
        { project with Id = newId; Name = (if String.IsNullOrWhiteSpace name then copyName project.Name else name.Trim()); Export = export }

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
        { Npc.Default with
            Id = nextId "npc" (allIds project)
            Name = (if String.IsNullOrWhiteSpace name then "New NPC" else name.Trim())
            X = Math.Floor(width / 2.0); Y = Math.Floor(height / 2.0)
            SceneId = (match scene with Some s -> s.Id | None -> "")
            CanMove = canMove; MovePattern = Some NpcMovePatterns.Stationary; Appearance = appearance }

    /// NPCEditor `createNPC` with the form defaults (villager, cannot move).
    let newNpc (project: GameProject) (name: string) : Npc = newNpcWith project name "villager" false

    /// NPCDetailEditor `createDialogue`: "Hello there!" with a single "Goodbye" option.
    let newDialogue (project: GameProject) (npcId: string) : Dialogue =
        { Id = nextId "dialogue" (allIds project); NpcId = npcId; Text = "Hello there!"
          Options = [ { DialogueOption.Default with Text = "Goodbye" } ]; Extra = [] }

    /// DialogueEditor `addOption`.
    let newDialogueOption () : DialogueOption = { DialogueOption.Default with Text = "New option" }

    /// NPC MovementScheduleSection "Add" schedule entry: 8:00 at the NPC's home tile.
    let newScheduleEntry (npc: Npc) : NpcScheduleEntry =
        { Minute = 8.0 * 60.0; SceneId = npc.SceneId; X = npc.X; Y = npc.Y; Extra = [] }

    /// ItemEditor form defaults: a stackable gift worth nothing; the id follows `custom-{type}-N`.
    let newItemOfType (project: GameProject) (itemType: string) : Item =
        { Item.Default with
            Id = nextId (sprintf "custom-%s" itemType) (allIds project); Name = "New Item"; Description = ""
            Type = itemType; Stackable = true; MaxStack = 99.0; Value = 0.0 }

    let newItem (project: GameProject) : Item = newItemOfType project ItemTypes.Gift

    /// CropEditor `handleCreateNew`.
    let newCrop (project: GameProject) : CustomCropDefinition =
        { CustomCropDefinition.Default with
            Id = nextId "custom" (allIds project); Name = "New Crop"; SeedCost = 10.0; BaseHarvestValue = 20.0
            GrowthTime = 15000.0; GrowthDays = Some 3.0; Stages = 4.0
            Seasons = [ firstSeasonId project ]; CanRegrow = false
            YieldMin = 1.0; YieldMax = 2.0; MutationChance = Some 0.01 }

    /// CropEditor `handleDuplicateCrop`: same values, new id, " Copy" name, no custom art.
    let duplicateCrop (project: GameProject) (crop: CustomCropDefinition) : CustomCropDefinition =
        { crop with Id = nextId "custom" (allIds project); Name = sprintf "%s Copy" crop.Name; CustomAsset = None }

    /// QuestEditor `handleCreateQuest`.
    let newQuest (project: GameProject) : Quest =
        { Quest.Default with
            Id = nextId "quest" (allIds project); Name = "New Quest"; Description = "Quest description"
            Status = QuestStatuses.NotStarted; AutoStart = Some false; Repeatable = Some false }

    /// QuestEditor `handleAddObjective`: a collect objective with no target yet.
    let newObjective (quest: Quest) : QuestObjective =
        { QuestObjective.Default with
            Id = nextId "obj" (quest.Objectives |> Seq.map (fun o -> o.Id)); Type = QuestObjectiveTypes.Collect
            Description = "New objective"; Completed = false; Progress = 0.0 }

    /// QuestEditor "Add Item Reward": the first item, quantity 1 (none when the project has no items).
    let newRewardItem (project: GameProject) : QuestRewardItem option =
        match Seq.tryHead project.Items with
        | Some item -> Some({ ItemId = item.Id; Quantity = 1.0 } : QuestRewardItem)
        | None -> None

    /// event-vocabulary.ts `defaultCondition`.
    let defaultCondition (kind: string) (project: GameProject) : EventCondition =
        match kind with
        | "enterTile" -> EventCondition.EnterTile { X = 0.0; Y = 0.0; X2 = None; Y2 = None }
        | "interactTile" -> EventCondition.InteractTile { X = 0.0; Y = 0.0; X2 = None; Y2 = None }
        | "inventorySpace" -> EventCondition.InventorySpace { ItemId = ""; Quantity = 1.0 }
        | "hasItem" -> EventCondition.HasItem { ItemId = ""; Quantity = 1.0 }
        | "dayRange" -> EventCondition.DayRange { MinDay = Some 1.0; MaxDay = None }
        | "season" -> EventCondition.Season { Seasons = [ firstSeasonId project ] }
        | "yearRange" -> EventCondition.YearRange { MinYear = Some 1.0; MaxYear = None }
        | "timeOfDay" -> EventCondition.TimeOfDay { MinMinute = 360.0; MaxMinute = 720.0 }
        | "questStatus" -> EventCondition.QuestStatus { QuestId = ""; Status = QuestStatuses.Completed }
        | "friendship" -> EventCondition.Friendship { NpcId = ""; Min = 250.0 }
        | "weather" -> EventCondition.Weather { WeatherIds = [ "rain" ] }
        | "festivalId" ->
            let festival =
                match List.tryHead project.Settings.Calendar.Festivals with
                | Some f -> f.Id
                | None -> ""
            EventCondition.FestivalId { FestivalId = festival }
        | _ -> EventCondition.Flag { Flag = ""; Value = true }

    /// event-forms.tsx outcome picker: an outcome of the kind with nothing filled in yet.
    let defaultOutcome (kind: string) : EventOutcome = { EventOutcome.Default with Type = kind }

    /// EventsEditor `addEvent`: on the start scene, on enter at (0,0), showing a message.
    let newEvent (project: GameProject) : GameEvent =
        { GameEvent.Default with
            Id = nextId "event" (allIds project); Name = "New Event"; SceneId = project.StartSceneId; Trigger = EventTriggers.Enter
            Conditions = [ EventCondition.EnterTile { X = 0.0; Y = 0.0; X2 = None; Y2 = None } ]
            Outcomes = [ { EventOutcome.Default with Type = EventOutcomeTypes.Message; Message = Some "Something happens…" } ]
            Active = true; Repeatable = false }

    /// ShopEditor `handleAddShop`.
    let newShop (project: GameProject) : ShopDefinition =
        { ShopDefinition.Default with
            Id = nextId "shop" (allIds project); Name = "New Shop"; SellPriceMultiplier = 1.0; BuysItems = true
            RepairsTools = false; RepairCostPerPoint = 0.5 }

    /// ShopEditor `handleAddStockEntry`: the first item (none when the project has no items).
    let newStockEntry (project: GameProject) : ShopStockEntry option =
        match Seq.tryHead project.Items with
        | Some item -> Some { ShopStockEntry.Default with ItemId = item.Id }
        | None -> None

    /// RecipeEditor "Recipe": a hand craft with nothing in or out yet.
    let newRecipe (project: GameProject) : RecipeDefinition =
        { RecipeDefinition.Default with Id = nextId "recipe" (allIds project); Name = "New Recipe"; ProcessingMinutes = 0.0; Category = "crafting" }

    /// RecipeEditor ingredient "+": the first item, quantity 1.
    let newIngredient (project: GameProject) : RecipeIngredient option =
        match Seq.tryHead project.Items with
        | Some item -> Some({ ItemId = item.Id; Quantity = 1.0 } : RecipeIngredient)
        | None -> None

    /// RecipeEditor "Machine".
    let newMachineType (project: GameProject) : MachineTypeDefinition =
        { MachineTypeDefinition.Default with
            Id = nextId "machine-custom" (allIds project); Name = "New Machine"; Description = ""; Color = "#9a7b4f"; BlocksMovement = true }

    /// NodeTypeEditor `addNodeType`: three axe hits, no drops, never respawns.
    let newNodeType (project: GameProject) : NodeTypeDefinition =
        { NodeTypeDefinition.Default with
            Id = nextId "node-custom" (allIds project); Name = "New Node"; Health = 3.0; RequiredTool = ToolTypes.Axe
            RequiredToolTier = 1.0; RespawnDays = Some None; Color = "#7a5a3a"; BlocksMovement = true }

    /// NodeTypeEditor drop "+": the first item, 1..1, weight 1.
    let newDrop (project: GameProject) : NodeDrop =
        { ItemId = firstItemId project; Min = 1.0; Max = 1.0; Weight = 1.0; Extra = [] }

    /// WildlifeEditor "Species": costs 500, eats hay when the project has it, makes the first item.
    let newAnimalSpecies (project: GameProject) : AnimalSpeciesDefinition =
        let feed = if project.Items |> List.exists (fun i -> i.Id = "feed-hay") then Some "feed-hay" else None
        { AnimalSpeciesDefinition.Default with
            Id = nextId "species" (allIds project); Name = "New Species"; PurchaseCost = 500.0; FeedItemId = feed
            ProductItemId = firstItemId project; ProductIntervalDays = 1.0; DaysToAdult = 3.0; Color = "#e8d8c3" }

    /// App.tsx animal placement: a newborn of the species, content, unfed, on the clicked tile.
    let newAnimal (project: GameProject) (speciesId: string) (sceneId: string) (x: int) (y: int) : AnimalState =
        let name =
            match project.AnimalSpecies |> Seq.tryFind (fun s -> s.Id = speciesId) with
            | Some species -> species.Name
            | None -> speciesId
        { AnimalState.Default with
            Id = nextId "animal" (allIds project); SpeciesId = speciesId; Name = name; SceneId = sceneId; X = float x; Y = float y
            Mood = 70.0; FedToday = false; PettedToday = false; AgeDays = 0.0; DaysSinceProduct = 0.0; ProductReady = false }

    /// WildlifeEditor "Table".
    let newFishTable (project: GameProject) : FishTable =
        { FishTable.Default with Id = nextId "fish-table" (allIds project); Name = "New Waters"; JunkChance = 0.15 }

    /// WildlifeEditor fish entry "+": the first item, weight 1, difficulty 0.3.
    let newFishEntry (project: GameProject) : FishTableEntry option =
        match Seq.tryHead project.Items with
        | Some item -> Some({ ItemId = item.Id; Weight = 1.0; Difficulty = 0.3 } : FishTableEntry)
        | None -> None

    /// ActionsEditor `addAction`: id from the name, one message outcome.
    let newAction (project: GameProject) : ActionDef =
        { ActionDef.Default with
            Id = slugId "New Action" (allIds project) "action"; Name = "New Action"; Description = ""; FailMessage = ""
            Outcomes = [ { EventOutcome.Default with Type = EventOutcomeTypes.Message; Message = Some "Something happens…" } ]
            EnergyCost = 0.0 }

    /// ActionsEditor `duplicateAction`: "(copy)" with a fresh slug id.
    let duplicateAction (project: GameProject) (action: ActionDef) : ActionDef =
        { action with Id = slugId (sprintf "%s copy" action.Name) (allIds project) "action"; Name = sprintf "%s (copy)" action.Name }

    /// ActionsEditor `TIMING_BAR_DEFAULTS`.
    let timingBarConfig () : (string * Json) list =
        [ "speed", JNumber 0.9
          "targetSize", JNumber 0.18
          "prompt", JString "Stop the marker in the zone!" ]

    /// ActionsEditor `addMinigame`: a timing bar with one catch-all tier.
    let newMinigame (project: GameProject) : MinigameDef =
        { MinigameDef.Default with
            Id = slugId "New Minigame" (allIds project) "minigame"; Name = "New Minigame"; Kind = "timing-bar"; Config = timingBarConfig ()
            ResultTiers = [ { MinScore = 0.0; Outcomes = [] } ] }

    /// ActionsEditor `duplicateMinigame`.
    let duplicateMinigame (project: GameProject) (minigame: MinigameDef) : MinigameDef =
        { minigame with Id = slugId (sprintf "%s copy" minigame.Name) (allIds project) "minigame"; Name = sprintf "%s (copy)" minigame.Name }

    /// MinigameEditor `addTier`.
    let newResultTier () : MinigameResultTier = { MinScore = 0.0; Outcomes = [] }

    /// SceneManager `createScene`: `createEmptyScene` filled with the chosen tile type (grass by default).
    let newSceneWith (project: GameProject) (name: string) (width: int) (height: int) (defaultTile: string) : Scene =
        let width = max 1 width
        let height = max 1 height
        let name = if String.IsNullOrWhiteSpace name then "New Scene" else name.Trim()
        let scene = AuthoringTiles.CreateEmptyScene(nextId "scene" (allIds project), name, float width, float height)
        if defaultTile = TileTypes.Grass then scene
        else Proj.mapTiles (fun tile -> AuthoringTiles.CreateEmptyTile(tile.X, tile.Y, defaultTile)) scene

    /// SceneManager `createScene` with the dialog defaults (16×12, grass).
    let newScene (project: GameProject) (name: string) (width: int) (height: int) : Scene = newSceneWith project name width height TileTypes.Grass

    /// TransitionEditor form defaults: from (0,0) to (0,0) of the chosen scene, unlocked.
    let newTransition (toSceneId: string) : SceneTransition =
        { SceneTransition.Default with FromX = 0.0; FromY = 0.0; ToSceneId = toSceneId; ToX = 0.0; ToY = 0.0 }

    /// TransitionEditor `createBidirectional`: the door plus its return door, as one edit.
    let linkScenes (fromSceneId: string) (transition: SceneTransition) : Edit =
        let back = { SceneTransition.Default with FromX = transition.ToX; FromY = transition.ToY; ToSceneId = fromSceneId; ToX = transition.FromX; ToY = transition.FromY }
        Batch("Link scenes", [ SetTransition(fromSceneId, transition); SetTransition(transition.ToSceneId, back) ])

    /// ProjectSettingsEditor `addSeason`: 28 days, id from the name.
    let newSeason (calendar: CalendarConfig) : CalendarSeason =
        { Id = slugId "New Season" (calendar.Seasons |> Seq.map (fun s -> s.Id)) "season"; Name = "New Season"; Days = 28.0 }

    /// ProjectSettingsEditor `addFestival`: day 1 of the first season (none without seasons).
    let newFestival (project: GameProject) (calendar: CalendarConfig) : CalendarFestival option =
        match Seq.tryHead calendar.Seasons with
        | None -> None
        | Some season ->
            Some
                { Id = nextId "festival" (allIds project |> Seq.append (calendar.Festivals |> Seq.map (fun f -> f.Id)))
                  Name = "New Festival"; SeasonId = season.Id; Day = 1.0 }

    /// ProjectSettingsEditor mine toggle: switching on fills in the default entrance (the start
    /// scene at 1,1) and depth bands where they are missing; switching off only clears the flag,
    /// so the settings come back when it is switched on again.
    let mineEnabled (project: GameProject) (enabled: bool) : MineConfig =
        let mine = project.Mine
        if not enabled then { mine with Enabled = false }
        else
            { mine with
                Enabled = true
                EntranceSceneId = Some(defaultArg mine.EntranceSceneId project.StartSceneId)
                EntranceX = Some(defaultArg mine.EntranceX 1.0)
                EntranceY = Some(defaultArg mine.EntranceY 1.0)
                Bands = (if mine.Bands.IsEmpty then Builtin.mineBands () else mine.Bands) }

    /// InterfaceEditor: an empty creator panel.
    let newGamePanel (project: GameProject) : GamePanel =
        let existing = defaultArg project.GamePanels [] |> List.map (fun p -> p.Id)
        { Id = nextId "panel" existing; Title = "New Panel"; VisibleFlag = None; Entries = [] }
