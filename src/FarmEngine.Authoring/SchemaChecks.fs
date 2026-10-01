namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// One failed schema check: zod's dotted issue path (`scenes.0.tiles.1.2.background`) and the
/// zod-style message.
type SchemaIssue =
    { Path: string
      Message: string }

    /// `path: message`, the string form the C# `SchemaValidation` and the migrations report.
    override this.ToString() = this.Path + ": " + this.Message

/// Port of `SchemaValidation.cs` (`ValidateProject`, `LintProject`, `ValidateExportedGame`).
///
/// Decoding (SchemaJson.fs) checks value kinds; it does not enforce zod refinements (int,
/// positive, min/max, enum membership, …). `projectIssues` re-checks exactly the constraints the
/// web's zod schemas enforce on parse, so a project the web version accepts loads here too. `projectLints` adds
/// the structural checks zod does not have (empty ids, duplicate scene ids, tile grid vs.
/// width/height, a dangling start scene, inverted regions and ranges): problems for the Problems
/// panel, never reasons to refuse a file. Not exhaustive.
///
/// Saves (`ValidateGameState`) are Rust-owned and not here.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SchemaChecks =
    /// Parse-level (what zod rejects) or lint-level (what the web editor lets a creator save).
    [<RequireQualifiedAccess>]
    type private Level =
        | Parse
        | Lint

    type private Finding = Level * SchemaIssue

    let private parse (path: string) (message: string) : Finding list = [ Level.Parse, { Path = path; Message = message } ]
    let private lint (path: string) (message: string) : Finding list = [ Level.Lint, { Path = path; Message = message } ]

    /// The checks for every element of a list, in order.
    let private each (list: 'T list) (check: int -> 'T -> Finding list) : Finding list =
        list |> List.mapi check |> List.concat

    let private whenSome (value: float option) (check: float -> Finding list) : Finding list =
        match value with
        | Some v -> check v
        | None -> []

    /// `Number.isInteger`.
    let isInteger (value: float) =
        not (Double.IsNaN value || Double.IsInfinity value) && Math.Floor value = value

    let private num (value: float) = JsNumber.format value

    // ── zod refinements ──────────────────────────────────────────────────────

    let private integer path (value: float) =
        if isInteger value then [] else parse path $"Expected integer, received {num value}"

    let private positiveInt path (value: float) =
        if not (isInteger value) then parse path $"Expected integer, received {num value}"
        elif value <= 0.0 then parse path "Number must be greater than 0"
        else []

    // `not (value > 0)` rather than `value <= 0`: NaN fails, like zod.
    let private positive path (value: float) =
        if not (value > 0.0) then parse path "Number must be greater than 0" else []

    let private nonNegative path (value: float) =
        if not (value >= 0.0) then parse path "Number must be greater than or equal to 0" else []

    let private range path (value: float) (min: float) (max: float) =
        if not (value >= min && value <= max) then parse path $"Number must be between {num min} and {num max}" else []

    /// zod `z.string()` accepts "": an empty id is a lint problem, not a parse error.
    let private nonEmpty path (value: string) =
        if String.IsNullOrEmpty value then lint path "Required id must be a non-empty string" else []

    let private enumValue path (value: string) (allowed: string list) =
        if List.contains value allowed then []
        else
            let expected = allowed |> List.map (fun a -> $"'{a}'") |> String.concat " | "
            parse path $"Invalid enum value. Expected {expected}, received '{value}'"

    /// A required `z.enum`.
    let private oneOf path (value: string) (allowed: string list) = enumValue path value allowed

    /// An `.optional()` `z.enum`.
    let private optionalOneOf path (value: string option) (allowed: string list) =
        match value with
        | None -> []
        | Some value -> enumValue path value allowed

    // ── Events, actions and minigames ────────────────────────────────────────

    let private region path (x: float) (y: float) (x2: float option) (y2: float option) =
        [ match x2 with
          | Some x2 when x2 < x -> yield! lint (path + ".x2") "x2 must be >= x"
          | _ -> ()
          match y2 with
          | Some y2 when y2 < y -> yield! lint (path + ".y2") "y2 must be >= y"
          | _ -> () ]

    let private conditions (basePath: string) (list: EventCondition list) =
        each list (fun c condition ->
            let cp = $"{basePath}.conditions.{c}"
            match condition with
            | EventCondition.EnterTile t -> region cp t.X t.Y t.X2 t.Y2
            | EventCondition.InteractTile t -> region cp t.X t.Y t.X2 t.Y2
            | EventCondition.HasItem h -> nonEmpty (cp + ".itemId") h.ItemId
            | EventCondition.InventorySpace s -> nonEmpty (cp + ".itemId") s.ItemId @ positiveInt (cp + ".quantity") s.Quantity
            | EventCondition.Flag f ->
                if String.IsNullOrEmpty f.Flag then lint (cp + ".flag") "Flag name must be a non-empty string" else []
            | EventCondition.DayRange d ->
                match d.MinDay, d.MaxDay with
                | Some minDay, Some maxDay when minDay > maxDay -> lint cp "minDay is greater than maxDay"
                | _ -> []
            | EventCondition.YearRange y ->
                match y.MinYear, y.MaxYear with
                | Some minYear, Some maxYear when minYear > maxYear -> lint cp "minYear is greater than maxYear"
                | _ -> []
            // A start after the end wraps past midnight (22:00–2:00), as the engine reads it.
            | EventCondition.TimeOfDay _ -> []
            | EventCondition.QuestStatus q -> nonEmpty (cp + ".questId") q.QuestId @ oneOf (cp + ".status") q.Status QuestStatuses.All
            | EventCondition.Friendship f -> nonEmpty (cp + ".npcId") f.NpcId
            | EventCondition.FestivalId f -> nonEmpty (cp + ".festivalId") f.FestivalId
            | EventCondition.Season _
            | EventCondition.Weather _ -> [])

    let private outcomes (basePath: string) (list: EventOutcome list) =
        each list (fun o outcome ->
            let op = $"{basePath}.outcomes.{o}"
            [ yield! oneOf (op + ".type") outcome.Type EventOutcomeTypes.All
              match outcome.Amount with
              | Some amount when Double.IsNaN amount || Double.IsInfinity amount -> yield! parse (op + ".amount") "Number must be finite"
              | _ -> ()
              yield! whenSome outcome.Radius (fun radius -> integer (op + ".radius") radius @ range (op + ".radius") radius 0.0 10.0)
              yield! optionalOneOf (op + ".newTileType") outcome.NewTileType TileTypes.All ])

    // ── Project sections, in the C# order ───────────────────────────────────

    let private tile (tp: string) (tile: Tile) =
        [ yield! oneOf (tp + ".type") tile.Type TileTypes.All
          yield! oneOf (tp + ".background") tile.Background TileTypes.All
          yield! optionalOneOf (tp + ".overlay") tile.Overlay TileTypes.All
          yield! optionalOneOf (tp + ".object") tile.Object TileTypes.All
          yield! optionalOneOf (tp + ".soilState") tile.SoilState SoilStates.All
          match tile.Crop with
          | None -> ()
          | Some crop ->
              yield! oneOf (tp + ".crop.quality") crop.Quality CropQualities.All
              yield! optionalOneOf (tp + ".crop.mutation") crop.Mutation CropMutations.All ]

    let private scene (scenes: Scene list) (s: int) (scene: Scene) =
        let sp = $"scenes.{s}"
        let duplicate =
            not (String.IsNullOrEmpty scene.Id) && scenes |> List.take s |> List.exists (fun earlier -> earlier.Id = scene.Id)
        let validWidth = isInteger scene.Width && scene.Width > 0.0
        let validHeight = isInteger scene.Height && scene.Height > 0.0
        [ yield! nonEmpty (sp + ".id") scene.Id
          if duplicate then yield! lint (sp + ".id") $"Duplicate scene id '{scene.Id}'"
          yield! positiveInt (sp + ".width") scene.Width
          yield! positiveInt (sp + ".height") scene.Height
          if validHeight && float scene.Tiles.Length <> scene.Height then
              yield! lint (sp + ".tiles") $"Expected {num scene.Height} rows (scene height), found {scene.Tiles.Length}"
          yield!
              each scene.Tiles (fun y row ->
                  let rp = $"{sp}.tiles.{y}"
                  [ if validWidth && float row.Length <> scene.Width then
                        yield! lint rp $"Expected {num scene.Width} tiles (scene width), found {row.Length}"
                    yield! each row (fun x t -> tile $"{rp}.{x}" t) ])
          yield! each scene.Transitions (fun t transition -> nonEmpty $"{sp}.transitions.{t}.toSceneId" transition.ToSceneId) ]

    let private scenes (p: GameProject) =
        [ yield! each p.Scenes (scene p.Scenes)
          let known = p.Scenes |> List.exists (fun s -> not (String.IsNullOrEmpty s.Id) && s.Id = p.StartSceneId)
          if not p.Scenes.IsEmpty && not known then yield! lint "startSceneId" $"No scene with id '{p.StartSceneId}'" ]

    let private player (p: GameProject) =
        [ yield! oneOf "player.direction" p.Player.Direction Directions.All
          yield! nonEmpty "player.sceneId" p.Player.SceneId
          yield! each p.Player.Inventory (fun i slot -> nonEmpty $"player.inventory.{i}.item.id" slot.Item.Id) ]

    let private items (p: GameProject) =
        each p.Items (fun i item ->
            let ip = $"items.{i}"
            [ yield! nonEmpty (ip + ".id") item.Id
              yield! oneOf (ip + ".type") item.Type ItemTypes.All
              yield! optionalOneOf (ip + ".toolType") item.ToolType ToolTypes.All
              yield! whenSome item.ToolTier (positiveInt (ip + ".toolTier")) ])

    let private npcs (p: GameProject) =
        each p.Npcs (fun i npc ->
            let np = $"npcs.{i}"
            [ yield! nonEmpty (np + ".id") npc.Id
              yield! optionalOneOf (np + ".movePattern") npc.MovePattern NpcMovePatterns.All
              yield! whenSome npc.WanderRadius (positiveInt (np + ".wanderRadius"))
              match npc.PatrolPoints with
              | None -> ()
              | Some points ->
                  yield!
                      each points (fun k point ->
                          integer $"{np}.patrolPoints.{k}.x" point.X @ integer $"{np}.patrolPoints.{k}.y" point.Y)
              match npc.Birthday with
              | None -> ()
              | Some birthday ->
                  // The season is checked against the project's calendar in ChecksContent (a
                  // warning: calendars are customizable).
                  yield! integer (np + ".birthday.day") birthday.Day
              yield! each npc.Dialogue (fun d dialogue -> nonEmpty $"{np}.dialogue.{d}.id" dialogue.Id) ])

    let private quests (p: GameProject) =
        each p.Quests (fun q quest ->
            let qp = $"quests.{q}"
            [ yield! nonEmpty (qp + ".id") quest.Id
              yield! oneOf (qp + ".status") quest.Status QuestStatuses.All
              yield!
                  each quest.Objectives (fun o objective ->
                      nonEmpty $"{qp}.objectives.{o}.id" objective.Id
                      @ oneOf $"{qp}.objectives.{o}.type" objective.Type QuestObjectiveTypes.All) ])

    let private events (p: GameProject) =
        each p.Events (fun e event ->
            let ep = $"events.{e}"
            [ yield! nonEmpty (ep + ".id") event.Id
              yield! oneOf (ep + ".trigger") event.Trigger EventTriggers.All
              yield! conditions ep event.Conditions
              yield! outcomes ep event.Outcomes ])

    let private actionsAndMinigames (p: GameProject) =
        [ yield!
              each p.Actions (fun a action ->
                  let ap = $"actions.{a}"
                  [ yield! nonEmpty (ap + ".id") action.Id
                    yield! nonNegative (ap + ".energyCost") action.EnergyCost
                    match action.Hotkey with
                    | Some hotkey when hotkey.Length > 1 -> yield! parse (ap + ".hotkey") "String must contain at most 1 character(s)"
                    | _ -> ()
                    yield! conditions ap action.Conditions
                    yield! outcomes ap action.Outcomes ])
          yield!
              each p.Minigames (fun m minigame ->
                  [ yield! nonEmpty $"minigames.{m}.id" minigame.Id
                    yield!
                        each minigame.ResultTiers (fun t tier ->
                            let tp = $"minigames.{m}.resultTiers.{t}"
                            range (tp + ".minScore") tier.MinScore 0.0 1.0 @ outcomes tp tier.Outcomes) ]) ]

    let private definitions (p: GameProject) =
        let idLists =
            [ "shops", p.Shops |> List.map (fun x -> x.Id)
              "recipes", p.Recipes |> List.map (fun x -> x.Id)
              "machineTypes", p.MachineTypes |> List.map (fun x -> x.Id)
              "animalSpecies", p.AnimalSpecies |> List.map (fun x -> x.Id)
              "fishTables", p.FishTables |> List.map (fun x -> x.Id)
              "animals", p.Animals |> List.map (fun x -> x.Id) ]
        [ for name, ids in idLists do
              for index, id in List.indexed ids do
                  yield! nonEmpty $"{name}.{index}.id" id
          yield!
              each p.NodeTypes (fun n node ->
                  let np = $"nodeTypes.{n}"
                  [ yield! nonEmpty (np + ".id") node.Id
                    yield! positiveInt (np + ".health") node.Health
                    yield! oneOf (np + ".requiredTool") node.RequiredTool ToolTypes.All
                    yield! positiveInt (np + ".requiredToolTier") node.RequiredToolTier
                    yield! whenSome (Option.flatten node.RespawnDays) (positiveInt (np + ".respawnDays")) ])
          yield!
              each p.Recipes (fun r recipe ->
                  [ yield! nonNegative $"recipes.{r}.processingMinutes" recipe.ProcessingMinutes
                    yield! each recipe.Inputs (fun k input -> positiveInt $"recipes.{r}.inputs.{k}.quantity" input.Quantity)
                    yield! each recipe.Outputs (fun k output -> positiveInt $"recipes.{r}.outputs.{k}.quantity" output.Quantity) ])
          yield!
              each p.Weather.Types (fun w weather ->
                  nonEmpty $"weather.types.{w}.id" weather.Id
                  @ range $"weather.types.{w}.cropDamageChance" weather.CropDamageChance 0.0 1.0)
          for season, entries in p.Weather.Table do
              yield! each entries (fun k entry -> positive $"weather.table.{season}.{k}.weight" entry.Weight) ]

    let private settings (p: GameProject) =
        let s = p.Settings
        [ yield! positive "settings.maxEnergy" s.MaxEnergy
          yield! range "settings.collapseEnergyFraction" s.CollapseEnergyFraction 0.0 1.0
          yield! nonNegative "settings.collapseMoneyPenalty" s.CollapseMoneyPenalty
          yield! positive "settings.movement.playerSpeed" s.Movement.PlayerSpeed
          yield! integer "settings.time.dayStartMinute" s.Time.DayStartMinute
          yield! nonNegative "settings.time.dayStartMinute" s.Time.DayStartMinute
          yield! integer "settings.time.dayEndMinute" s.Time.DayEndMinute
          // An end at or before the start collapsed the player on every tick (#24).
          if s.Time.DayEndMinute - s.Time.DayStartMinute < SettingsSchema.MinDayWindowMinutes then
              yield!
                  parse
                      "settings.time.dayEndMinute"
                      $"The day must end at least {num SettingsSchema.MinDayWindowMinutes} minutes after it starts ({num s.Time.DayStartMinute})"
          // The clock counts micro-minutes in 32 bits: a later end never comes.
          if s.Time.DayEndMinute > SettingsSchema.MaxDayEndMinute then
              yield! parse "settings.time.dayEndMinute" $"Number must be less than or equal to {num SettingsSchema.MaxDayEndMinute}"
          yield! positive "settings.time.minutesPerRealSecond" s.Time.MinutesPerRealSecond
          if s.Time.MinutesPerRealSecond > SettingsSchema.MaxMinutesPerRealSecond then
              yield!
                  parse "settings.time.minutesPerRealSecond" $"Number must be less than or equal to {num SettingsSchema.MaxMinutesPerRealSecond}"
          yield!
              each s.Calendar.Seasons (fun c season ->
                  nonEmpty $"settings.calendar.seasons.{c}.id" season.Id
                  @ positiveInt $"settings.calendar.seasons.{c}.days" season.Days)
          yield!
              each s.Calendar.Festivals (fun f festival ->
                  nonEmpty $"settings.calendar.festivals.{f}.id" festival.Id
                  @ positiveInt $"settings.calendar.festivals.{f}.day" festival.Day) ]

    let private mine (p: GameProject) =
        let m = p.Mine
        [ yield! positiveInt "mine.floors" m.Floors
          yield! positiveInt "mine.floorWidth" m.FloorWidth
          yield! positiveInt "mine.floorHeight" m.FloorHeight
          yield! positiveInt "mine.elevatorEvery" m.ElevatorEvery
          yield! range "mine.ladderChance" m.LadderChance 0.0 1.0
          yield!
              each m.Bands (fun b band ->
                  [ yield! positiveInt $"mine.bands.{b}.fromFloor" band.FromFloor
                    yield! positiveInt $"mine.bands.{b}.toFloor" band.ToFloor
                    yield! range $"mine.bands.{b}.density" band.Density 0.0 1.0 ])
          yield! whenSome p.MineDeepestFloor (fun deepest -> integer "mineDeepestFloor" deepest @ nonNegative "mineDeepestFloor" deepest) ]

    /// The `rngState` literal and tuple, from its algorithm and state words.
    let private rngState (algorithm: string option) (words: uint32 list option) =
        [ if algorithm <> Some "xoshiro128ss" then yield! parse "rngState.algorithm" "Invalid literal value, expected \"xoshiro128ss\""
          let isTuple =
              match words with
              | None -> false
              | Some words -> words.Length = 4
          if not isTuple then yield! parse "rngState.s" "Expected a tuple of 4 integers" ]

    let private packsAndRng (p: GameProject) =
        [ yield!
              each p.ContentPacks (fun i install ->
                  if PackRules.isValidPackId install.Pack.Manifest.Id then []
                  else parse $"contentPacks.{i}.pack.manifest.id" "pack ids must be lowercase letters, digits and dashes")
          match p.RngState with
          | None -> ()
          | Some rng -> yield! rngState (Some rng.Algorithm) (Some rng.S) ]

    /// Every finding, parse- and lint-level interleaved in the C# order.
    let private findings (p: GameProject) : Finding list =
        [ yield! integer "schemaVersion" p.SchemaVersion
          yield! nonEmpty "id" p.Id
          yield! oneOf "mode" p.Mode EditorModes.All
          yield! oneOf "selectedTileType" p.SelectedTileType TileTypes.All
          yield! integer "currentYear" p.CurrentYear
          yield! scenes p
          yield! player p
          yield! items p
          yield! npcs p
          yield! each p.Dialogues (fun d dialogue -> nonEmpty $"dialogues.{d}.id" dialogue.Id)
          yield! quests p
          yield! events p
          yield! actionsAndMinigames p
          yield! definitions p
          yield! settings p
          yield! mine p
          yield! packsAndRng p ]

    let private at (level: Level) (all: Finding list) : SchemaIssue list =
        all |> List.choose (fun (l, issue) -> if l = level then Some issue else None)

    /// Parse-level checks only (what zod rejects; C# `SchemaValidation.ValidateProject`).
    /// Import and migrations use this.
    let projectIssues (project: GameProject) : SchemaIssue list = findings project |> at Level.Parse

    /// Structural lint zod cannot express (C# `SchemaValidation.LintProject`). The web editor
    /// lets creators save all of these, so they are reported, not rejected.
    let projectLints (project: GameProject) : SchemaIssue list = findings project |> at Level.Lint

    // ── Exported games ───────────────────────────────────────────────────────

    /// The checks on a `rngState` key an exported game carries as an undeclared (passthrough) key:
    /// the same checks as the project's own `rngState`, the last check, so appending keeps the
    /// order. A value that is not an object, or whose fields have the wrong kind, is skipped (the
    /// export keeps it as it is).
    let private passthroughRngState (extra: (string * Json) list) : Finding list =
        let word (value: Json) : uint32 option =
            match value with
            | JNumber n when n = Math.Floor n && n >= 0.0 && n <= 4294967295.0 -> Some(uint32 n)
            | _ -> None
        let words (items: Json list) : uint32 list option =
            let values = items |> List.map word
            if List.forall Option.isSome values then Some(List.choose id values) else None
        match extra |> List.tryFind (fun (key, _) -> key = "rngState") with
        | Some(_, (JObject _ as rng)) ->
            let algorithm : string option option =
                match Json.tryGet "algorithm" rng with
                | None -> Some(Some "xoshiro128ss")
                | Some(JString value) -> Some(Some value)
                | Some JNull -> Some None
                | _ -> None
            let s : uint32 list option option =
                match Json.tryGet "s" rng with
                | None -> Some(Some [ 0u; 0u; 0u; 0u ])
                | Some(JArray items) -> words items |> Option.map Some
                | Some JNull -> Some None
                | _ -> None
            match algorithm, s with
            | Some algorithm, Some s -> rngState algorithm s
            | _ -> []
        | _ -> []

    /// The exported game as the project it is validated as: its own fields plus fixed editor-only
    /// fields and, when it carries no player, a skeleton player (`rngState` is checked by
    /// `passthroughRngState`).
    let private asProject (game: ExportedGame) : GameProject =
        { GameProject.Default with
            SchemaVersion = defaultArg game.SchemaVersion 0.0
            Id = "exported-game"
            Mode = EditorModes.Play
            SelectedTileType = TileTypes.Grass
            Version = game.Version
            Name = game.Name
            Scenes = game.Scenes
            Npcs = game.Npcs
            Items = game.Items
            Events = game.Events
            Dialogues = game.Dialogues
            Quests = game.Quests
            StartSceneId = game.StartSceneId
            CustomAssets = game.CustomAssets
            CustomCrops = game.CustomCrops
            PlayerCustomImage = game.PlayerCustomImage
            PlayerVisual = game.PlayerVisual
            Graphics = game.Graphics
            GamePanels = game.GamePanels
            CurrentSeason = game.CurrentSeason
            CurrentDay = game.CurrentDay
            CurrentTimeMinutes = game.CurrentTimeMinutes
            CurrentYear = game.CurrentYear
            GameStartTime = game.GameStartTime
            Shops = game.Shops
            NodeTypes = game.NodeTypes
            Settings = game.Settings
            Recipes = game.Recipes
            MachineTypes = game.MachineTypes
            Weather = game.Weather
            AnimalSpecies = game.AnimalSpecies
            Animals = game.Animals
            FishTables = game.FishTables
            Mine = game.Mine
            Actions = game.Actions
            Minigames = game.Minigames
            ContentPacks = game.ContentPacks
            Player = defaultArg game.Player { Player.Default with Direction = Directions.Down; SceneId = "exported-game" }
            CurrentWeatherId = game.CurrentWeatherId
            SocialState = game.SocialState
            MineDeepestFloor = game.MineDeepestFloor
            QuarantinedItems = game.QuarantinedItems }

    /// The checks of the project an exported game is validated as: `projectIssues` minus the
    /// editor-only fields (`id`, `mode`, `selectedTileType`) and, when the game carries no player,
    /// the player.
    let exportedProjectIssues (hasPlayer: bool) (project: GameProject) : SchemaIssue list =
        let editorOnly (issue: SchemaIssue) =
            match issue.Path with
            | "id" | "mode" | "selectedTileType" -> true
            | path -> not hasPlayer && path.StartsWith("player.", StringComparison.Ordinal)
        projectIssues project |> List.filter (editorOnly >> not)

    /// `projectIssues` for an `ExportedGame` (web `validateExportedGame`): the same checks minus
    /// the editor-only fields and the player when the export carries none.
    let exportedGameIssues (game: ExportedGame) : SchemaIssue list =
        exportedProjectIssues game.Player.IsSome (asProject game) @ (passthroughRngState game.Extra |> at Level.Parse)

    let private strings (issues: SchemaIssue list) = issues |> List.map string

    /// `projectIssues` as `path: message` strings .
    let validateProject (project: GameProject) : string list = projectIssues project |> strings

    /// `projectLints` as `path: message` strings (C# `SchemaValidation.LintProject`).
    let lintProject (project: GameProject) : string list = projectLints project |> strings

    /// `exportedGameIssues` as `path: message` strings (C# `SchemaValidation.ValidateExportedGame`).
    let validateExportedGame (game: ExportedGame) : string list = exportedGameIssues game |> strings
