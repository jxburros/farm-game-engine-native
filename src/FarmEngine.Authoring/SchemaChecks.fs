namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open System.Text.Json
open FarmEngine.Json
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
/// The C# schema records don't enforce zod refinements (int, positive, min/max, enum
/// membership, …). `projectIssues` re-checks exactly the constraints the web's zod schemas
/// enforce on parse, so a project the web version accepts loads here too. `projectLints` adds
/// the structural checks zod does not have (empty ids, duplicate scene ids, tile grid vs.
/// width/height, a dangling start scene, inverted regions and ranges): problems for the Problems
/// panel, never reasons to refuse a file. Not exhaustive.
///
/// The checks read the records exactly as the C# does (list counts and indexers, LINQ-style
/// sequence functions where the C# uses LINQ), so a `null` where the schema has none fails with
/// the same exception as the C# version. Saves (`ValidateGameState`) are Rust-owned and not here.
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

    /// `x is null` for a record or list the C# declares non-nullable (JSON `null` still gets there).
    let private missing (value: 'T) = obj.ReferenceEquals(value, null)

    /// The checks for every element of a list, in order (a `for` over `Count`, as in the C#).
    let private each (list: List<'T>) (check: int -> 'T -> Finding list) : Finding list =
        [ for i in 0 .. list.Count - 1 do yield! check i list[i] ]

    let private whenSome (value: Nullable<float>) (check: float -> Finding list) : Finding list =
        if value.HasValue then check value.Value else []

    // ── zod refinements ──────────────────────────────────────────────────────

    let private integer path (value: float) =
        if Js.IsInteger value then [] else parse path $"Expected integer, received {Js.Num value}"

    let private positiveInt path (value: float) =
        if not (Js.IsInteger value) then parse path $"Expected integer, received {Js.Num value}"
        elif value <= 0.0 then parse path "Number must be greater than 0"
        else []

    // `not (value > 0)` rather than `value <= 0`: NaN fails, like zod.
    let private positive path (value: float) =
        if not (value > 0.0) then parse path "Number must be greater than 0" else []

    let private nonNegative path (value: float) =
        if not (value >= 0.0) then parse path "Number must be greater than or equal to 0" else []

    let private range path (value: float) (min: float) (max: float) =
        if not (value >= min && value <= max) then parse path $"Number must be between {Js.Num min} and {Js.Num max}" else []

    /// zod `z.string()` accepts "": an empty id is a lint problem, not a parse error.
    let private nonEmpty path (value: string | null) =
        if String.IsNullOrEmpty value then lint path "Required id must be a non-empty string" else []

    let private enumValue path (value: string) (allowed: IReadOnlyList<string>) =
        if Seq.contains value allowed then []
        else
            let expected = allowed |> Seq.map (fun a -> $"'{a}'") |> String.concat " | "
            parse path $"Invalid enum value. Expected {expected}, received '{value}'"

    /// A required `z.enum`.
    let private oneOf path (value: string | null) (allowed: IReadOnlyList<string>) =
        match value with
        | null -> parse path "Required"
        | value -> enumValue path value allowed

    /// An `.optional()` `z.enum`.
    let private optionalOneOf path (value: string | null) (allowed: IReadOnlyList<string>) =
        match value with
        | null -> []
        | value -> enumValue path value allowed

    // ── Events, actions and minigames ────────────────────────────────────────

    let private region path (x: float) (y: float) (x2: Nullable<float>) (y2: Nullable<float>) =
        [ if x2.HasValue && x2.Value < x then yield! lint (path + ".x2") "x2 must be >= x"
          if y2.HasValue && y2.Value < y then yield! lint (path + ".y2") "y2 must be >= y" ]

    let private conditions (basePath: string) (list: List<EventCondition>) =
        each list (fun c condition ->
            let cp = $"{basePath}.conditions.{c}"
            match condition with
            | condition when missing condition -> parse cp "Expected object, received null"
            | :? EnterTileCondition as t -> region cp t.X t.Y t.X2 t.Y2
            | :? InteractTileCondition as t -> region cp t.X t.Y t.X2 t.Y2
            | :? HasItemCondition as h -> nonEmpty (cp + ".itemId") h.ItemId
            | :? InventorySpaceCondition as s -> nonEmpty (cp + ".itemId") s.ItemId @ positiveInt (cp + ".quantity") s.Quantity
            | :? FlagCondition as f ->
                if String.IsNullOrEmpty f.Flag then lint (cp + ".flag") "Flag name must be a non-empty string" else []
            | :? DayRangeCondition as d ->
                if d.MinDay.HasValue && d.MaxDay.HasValue && d.MinDay.Value > d.MaxDay.Value then lint cp "minDay is greater than maxDay" else []
            | :? YearRangeCondition as y ->
                if y.MinYear.HasValue && y.MaxYear.HasValue && y.MinYear.Value > y.MaxYear.Value then lint cp "minYear is greater than maxYear" else []
            | :? TimeOfDayCondition as t -> if t.MinMinute > t.MaxMinute then lint cp "minMinute is greater than maxMinute" else []
            | :? QuestStatusCondition as q -> nonEmpty (cp + ".questId") q.QuestId @ oneOf (cp + ".status") q.Status QuestStatuses.All
            | :? FriendshipCondition as f -> nonEmpty (cp + ".npcId") f.NpcId
            | :? FestivalIdCondition as f -> nonEmpty (cp + ".festivalId") f.FestivalId
            | _ -> [])

    let private outcomes (basePath: string) (list: List<EventOutcome>) =
        each list (fun o outcome ->
            let op = $"{basePath}.outcomes.{o}"
            if missing outcome then
                parse op "Expected object, received null"
            else
                [ yield! oneOf (op + ".type") outcome.Type EventOutcomeTypes.All
                  if outcome.Amount.HasValue && not (Double.IsFinite outcome.Amount.Value) then
                      yield! parse (op + ".amount") "Number must be finite"
                  yield! whenSome outcome.Radius (fun radius -> integer (op + ".radius") radius @ range (op + ".radius") radius 0.0 10.0)
                  yield! optionalOneOf (op + ".newTileType") outcome.NewTileType TileTypes.All ])

    // ── Project sections, in the C# order ───────────────────────────────────

    let private tile (tp: string) (tile: Tile) =
        if missing tile then
            parse tp "Expected object, received null"
        else
            [ yield! oneOf (tp + ".type") tile.Type TileTypes.All
              yield! oneOf (tp + ".background") tile.Background TileTypes.All
              yield! optionalOneOf (tp + ".overlay") tile.Overlay TileTypes.All
              yield! optionalOneOf (tp + ".object") tile.Object TileTypes.All
              yield! optionalOneOf (tp + ".soilState") tile.SoilState SoilStates.All
              match Option.ofObj tile.Crop with
              | None -> ()
              | Some crop ->
                  yield! oneOf (tp + ".crop.quality") crop.Quality CropQualities.All
                  yield! optionalOneOf (tp + ".crop.mutation") crop.Mutation CropMutations.All ]

    let private scene (scenes: List<Scene>) (s: int) (scene: Scene) =
        let sp = $"scenes.{s}"
        let duplicate =
            not (String.IsNullOrEmpty scene.Id) && scenes |> Seq.take s |> Seq.exists (fun earlier -> earlier.Id = scene.Id)
        let validWidth = Js.IsInteger scene.Width && scene.Width > 0.0
        let validHeight = Js.IsInteger scene.Height && scene.Height > 0.0
        [ yield! nonEmpty (sp + ".id") scene.Id
          if duplicate then yield! lint (sp + ".id") $"Duplicate scene id '{scene.Id}'"
          yield! positiveInt (sp + ".width") scene.Width
          yield! positiveInt (sp + ".height") scene.Height
          if validHeight && float scene.Tiles.Count <> scene.Height then
              yield! lint (sp + ".tiles") $"Expected {Js.Num scene.Height} rows (scene height), found {scene.Tiles.Count}"
          yield!
              each scene.Tiles (fun y row ->
                  let rp = $"{sp}.tiles.{y}"
                  if missing row then
                      parse rp "Expected array, received null"
                  else
                      [ if validWidth && float row.Count <> scene.Width then
                            yield! lint rp $"Expected {Js.Num scene.Width} tiles (scene width), found {row.Count}"
                        yield! each row (fun x t -> tile $"{rp}.{x}" t) ])
          yield! each scene.Transitions (fun t transition -> nonEmpty $"{sp}.transitions.{t}.toSceneId" transition.ToSceneId) ]

    let private scenes (p: GameProject) =
        [ yield! each p.Scenes (scene p.Scenes)
          let known = p.Scenes |> Seq.exists (fun s -> not (String.IsNullOrEmpty s.Id) && s.Id = p.StartSceneId)
          if p.Scenes.Count > 0 && not known then yield! lint "startSceneId" $"No scene with id '{p.StartSceneId}'" ]

    let private player (p: GameProject) =
        let itemId (slot: InventorySlot) : string | null = if missing slot.Item then null else slot.Item.Id
        [ yield! oneOf "player.direction" p.Player.Direction Directions.All
          yield! nonEmpty "player.sceneId" p.Player.SceneId
          yield! each p.Player.Inventory (fun i slot -> nonEmpty $"player.inventory.{i}.item.id" (itemId slot)) ]

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
              match Option.ofObj npc.PatrolPoints with
              | None -> ()
              | Some points ->
                  yield!
                      each points (fun k point ->
                          integer $"{np}.patrolPoints.{k}.x" point.X @ integer $"{np}.patrolPoints.{k}.y" point.Y)
              match Option.ofObj npc.Birthday with
              | None -> ()
              | Some birthday ->
                  yield! oneOf (np + ".birthday.season") birthday.Season PrimitivesSchema.ClassicSeasons
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
                    match Option.ofObj action.Hotkey with
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
        // The C# projects the ids of all six lists before walking any of them (LINQ `Select`).
        let idLists =
            [ "shops", p.Shops |> Seq.map (fun x -> x.Id)
              "recipes", p.Recipes |> Seq.map (fun x -> x.Id)
              "machineTypes", p.MachineTypes |> Seq.map (fun x -> x.Id)
              "animalSpecies", p.AnimalSpecies |> Seq.map (fun x -> x.Id)
              "fishTables", p.FishTables |> Seq.map (fun x -> x.Id)
              "animals", p.Animals |> Seq.map (fun x -> x.Id) ]
        [ for name, ids in idLists do
              for index, id in Seq.indexed ids do
                  yield! nonEmpty $"{name}.{index}.id" id
          yield!
              each p.NodeTypes (fun n node ->
                  let np = $"nodeTypes.{n}"
                  [ yield! nonEmpty (np + ".id") node.Id
                    yield! positiveInt (np + ".health") node.Health
                    yield! oneOf (np + ".requiredTool") node.RequiredTool ToolTypes.All
                    yield! positiveInt (np + ".requiredToolTier") node.RequiredToolTier
                    yield! whenSome node.RespawnDays (positiveInt (np + ".respawnDays")) ])
          yield!
              each p.Recipes (fun r recipe ->
                  [ yield! nonNegative $"recipes.{r}.processingMinutes" recipe.ProcessingMinutes
                    yield! each recipe.Inputs (fun k input -> positiveInt $"recipes.{r}.inputs.{k}.quantity" input.Quantity)
                    yield! each recipe.Outputs (fun k output -> positiveInt $"recipes.{r}.outputs.{k}.quantity" output.Quantity) ])
          yield!
              each p.Weather.Types (fun w weather ->
                  nonEmpty $"weather.types.{w}.id" weather.Id
                  @ range $"weather.types.{w}.cropDamageChance" weather.CropDamageChance 0.0 1.0)
          for KeyValue(season, entries) in p.Weather.Table do
              yield! each entries (fun k entry -> positive $"weather.table.{season}.{k}.weight" entry.Weight) ]

    let private settings (p: GameProject) =
        let s = p.Settings
        [ yield! positive "settings.maxEnergy" s.MaxEnergy
          yield! range "settings.collapseEnergyFraction" s.CollapseEnergyFraction 0.0 1.0
          yield! nonNegative "settings.collapseMoneyPenalty" s.CollapseMoneyPenalty
          yield! positive "settings.movement.playerSpeed" s.Movement.PlayerSpeed
          yield! integer "settings.time.dayStartMinute" s.Time.DayStartMinute
          yield! integer "settings.time.dayEndMinute" s.Time.DayEndMinute
          yield! positive "settings.time.minutesPerRealSecond" s.Time.MinutesPerRealSecond
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
    let private rngState (algorithm: string | null) (words: uint32 array | null) =
        [ if algorithm <> "xoshiro128ss" then yield! parse "rngState.algorithm" "Invalid literal value, expected \"xoshiro128ss\""
          let isTuple =
              match words with
              | null -> false
              | words -> words.Length = 4
          if not isTuple then yield! parse "rngState.s" "Expected a tuple of 4 integers" ]

    let private packsAndRng (p: GameProject) =
        [ yield!
              each p.ContentPacks (fun i install ->
                  if PacksSchema.IsValidPackId install.Pack.Manifest.Id then []
                  else parse $"contentPacks.{i}.pack.manifest.id" "pack ids must be lowercase letters, digits and dashes")
          match Option.ofObj p.RngState with
          | None -> ()
          | Some rng -> yield! rngState rng.Algorithm rng.S ]

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

    /// The checks on a `rngState` key an exported game carries as an unknown (passthrough) key.
    /// The C# checks it because its JSON round trip turns the key into `GameProject.RngState`
    /// (the last check, so appending keeps the order); this reads it the way that round trip does.
    /// A value the round trip cannot deserialize (the C# throws) is skipped.
    let private passthroughRngState (extra: Dictionary<string, JsonElement> | null) : Finding list =
        let word (element: JsonElement) : uint32 option =
            if element.ValueKind <> JsonValueKind.Number then None
            else
                match element.TryGetUInt32() with
                | true, value -> Some value
                | _ -> None
        let words (element: JsonElement) : (uint32 array | null) option =
            let values = [ for e in element.EnumerateArray() -> word e ]
            if List.forall Option.isSome values then Some(values |> List.choose id |> Array.ofList) else None
        let rng =
            match extra with
            | null -> None
            | extra ->
                match extra.TryGetValue "rngState" with
                | true, element when element.ValueKind = JsonValueKind.Object ->
                    let algorithm : (string | null) option =
                        match element.TryGetProperty "algorithm" with
                        | false, _ -> Some "xoshiro128ss"
                        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                        | true, value when value.ValueKind = JsonValueKind.Null -> Some null
                        | _ -> None
                    let s : (uint32 array | null) option =
                        match element.TryGetProperty "s" with
                        | false, _ -> Some(Array.zeroCreate 4)
                        | true, value when value.ValueKind = JsonValueKind.Array -> words value
                        | true, value when value.ValueKind = JsonValueKind.Null -> Some null
                        | _ -> None
                    Option.map2 (fun algorithm s -> algorithm, s) algorithm s
                | _ -> None
        match rng with
        | Some(algorithm, s) -> rngState algorithm s
        | None -> []

    /// The exported game as the project the C# validates: its own fields plus fixed editor-only
    /// fields and, when it carries no player, a skeleton player (`rngState` is checked by
    /// `passthroughRngState`).
    let private asProject (game: ExportedGame) : GameProject =
        GameProject(
            SchemaVersion = (if game.SchemaVersion.HasValue then game.SchemaVersion.Value else 0.0),
            Id = "exported-game",
            Mode = EditorModes.Play,
            SelectedTileType = TileTypes.Grass,
            Version = game.Version,
            Name = game.Name,
            Scenes = game.Scenes,
            Npcs = game.Npcs,
            Items = game.Items,
            Events = game.Events,
            Dialogues = game.Dialogues,
            Quests = game.Quests,
            StartSceneId = game.StartSceneId,
            CustomAssets = game.CustomAssets,
            CustomCrops = game.CustomCrops,
            PlayerCustomImage = game.PlayerCustomImage,
            PlayerVisual = game.PlayerVisual,
            Graphics = game.Graphics,
            GamePanels = game.GamePanels,
            CurrentSeason = game.CurrentSeason,
            CurrentDay = game.CurrentDay,
            CurrentTimeMinutes = game.CurrentTimeMinutes,
            CurrentYear = game.CurrentYear,
            GameStartTime = game.GameStartTime,
            Shops = game.Shops,
            NodeTypes = game.NodeTypes,
            Settings = game.Settings,
            Recipes = game.Recipes,
            MachineTypes = game.MachineTypes,
            Weather = game.Weather,
            AnimalSpecies = game.AnimalSpecies,
            Animals = game.Animals,
            FishTables = game.FishTables,
            Mine = game.Mine,
            Actions = game.Actions,
            Minigames = game.Minigames,
            ContentPacks = game.ContentPacks,
            Player =
                (match game.Player with
                 | null -> Player(Direction = Directions.Down, SceneId = "exported-game")
                 | player -> player),
            CurrentWeatherId = game.CurrentWeatherId,
            SocialState = game.SocialState,
            MineDeepestFloor = game.MineDeepestFloor,
            QuarantinedItems = game.QuarantinedItems
        )

    /// The checks of the project an exported game is validated as: `projectIssues` minus the
    /// editor-only fields (`id`, `mode`, `selectedTileType`) and, when the game carries no player,
    /// the player.
    let exportedProjectIssues (hasPlayer: bool) (project: GameProject) : SchemaIssue list =
        let editorOnly (issue: SchemaIssue) =
            match issue.Path with
            | "id" | "mode" | "selectedTileType" -> true
            | path -> not hasPlayer && path.StartsWith("player.", StringComparison.Ordinal)
        projectIssues project |> List.filter (editorOnly >> not)

    /// `projectIssues` for an `ExportedGame` (C# `SchemaValidation.ValidateExportedGame`): the
    /// same checks minus the editor-only fields and the player when the export carries none.
    ///
    /// The C# builds the project with a JSON round trip, which also turns a `null` in a required
    /// field into its default and throws on NaN or a mistyped passthrough key; this reads the
    /// fields as they are (a `null` enum is "Required", as in `projectIssues`). On .NET,
    /// `FarmEngine.Authoring.Net.ProjectMigrations.validateExportedGame` runs these checks on the
    /// C# round trip and matches the C# in those cases too.
    let exportedGameIssues (game: ExportedGame) : SchemaIssue list =
        exportedProjectIssues (not (isNull game.Player)) (asProject game) @ (passthroughRngState game.Extra |> at Level.Parse)

    let private strings (issues: SchemaIssue list) = issues |> List.map string

    /// `projectIssues` as `path: message` strings (C# `SchemaValidation.ValidateProject`).
    let validateProject (project: GameProject) : string list = projectIssues project |> strings

    /// `projectLints` as `path: message` strings (C# `SchemaValidation.LintProject`).
    let lintProject (project: GameProject) : string list = projectLints project |> strings

    /// `exportedGameIssues` as `path: message` strings (C# `SchemaValidation.ValidateExportedGame`).
    let validateExportedGame (game: ExportedGame) : string list = exportedGameIssues game |> strings
