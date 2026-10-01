/// Deliberately broken projects for the validation tests: one case per check in `SchemaChecks`
/// (`validateProject`, `lintProject`) and `ContentLints` (`validateProjectContent`), each built
/// from the starter farm by editing its JSON, with the finding it must produce. Plus malformed
/// shapes (nulls where the schema has none) that the typed parse refuses.
module FarmEngine.Authoring.Tests.BrokenProjects

open System
open System.Text.Json.Nodes
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Schemas

/// What a case must produce (and so proves the check is reached).
type Expect =
    /// A `validateProject` entry starting with this (`path: message`).
    | Parse of string
    /// A `lintProject` entry starting with this.
    | Lint of string
    /// A `validateProjectContent` message containing this.
    | Content of string
    /// No `validateProjectContent` message contains this.
    | NoContent of string
    /// The typed parse refuses the project with this issue (`path: message`).
    | Decode of string

type Case =
    { Name: string
      /// Edits to the starter farm's JSON.
      Edit: JsonObject -> unit
      /// Edits to the typed project after parsing (values JSON cannot carry, such as NaN).
      Typed: GameProject -> GameProject
      Expect: Expect list }

// ── JSON editing ─────────────────────────────────────────────────────────────

let private child (node: JsonNode) (segment: string) : JsonNode =
    match node with
    | :? JsonArray as array -> array.[int segment]
    | _ -> node.[segment]

let private parentAndKey (root: JsonObject) (path: string) =
    let segments = path.Split('.')
    let parent = segments.[.. segments.Length - 2] |> Array.fold child (root :> JsonNode)
    parent, segments.[segments.Length - 1]

/// Sets `path` (dotted, array indexes as numbers) to a JSON literal (`"null"` for null).
let set (path: string) (json: string) (root: JsonObject) =
    let parent, key = parentAndKey root path
    let value = JsonNode.Parse json
    match parent with
    | :? JsonArray as array -> array.[int key] <- value
    | _ -> parent.AsObject().[key] <- value

/// Appends a JSON literal to the array at `path`.
let add (path: string) (json: string) (root: JsonObject) =
    let target = if path = "" then root :> JsonNode else path.Split('.') |> Array.fold child (root :> JsonNode)
    target.AsArray().Add(JsonNode.Parse json)

/// Removes an array element or an object key.
let remove (path: string) (root: JsonObject) =
    let parent, key = parentAndKey root path
    match parent with
    | :? JsonArray as array -> array.RemoveAt(int key)
    | _ -> parent.AsObject().Remove key |> ignore

let private all (edits: (JsonObject -> unit) list) (root: JsonObject) =
    for edit in edits do
        edit root

let private case name expect edits =
    { Name = name; Edit = all edits; Typed = id; Expect = expect }

/// A case with a null in a required field: the typed parse refuses it.
let private nullCase name (decode: string) edits = case name [ Decode decode ] edits

let private malformed name (decode: string) edits = case name [ Decode decode ] edits

/// An event with the given conditions and outcomes (JSON array bodies).
let private event (id: string) (conditions: string) (outcomes: string) =
    sprintf """{"id":"%s","name":"Broken %s","sceneId":"scene-farm","trigger":"enter","conditions":[%s],"outcomes":[%s],"active":true,"repeatable":false}"""
        id id conditions outcomes

let private addEvent conditions outcomes = add "events" (event "ev-broken" conditions outcomes)

let private scene (id: string) (name: string) (extra: string) =
    sprintf """{"id":"%s","name":"%s","width":2,"height":1,"tiles":[[{"x":0,"y":0,"type":"grass","background":"grass","collision":false,"soilMoisture":0,"soilFertility":0},{"x":1,"y":0,"type":"grass","background":"grass","collision":false,"soilMoisture":0,"soilFertility":0}]],"transitions":[],"npcs":[],"events":[]%s}"""
        id name extra

let private crop (cropType: string) (quality: string) (mutation: string) =
    let mutation = if mutation = "" then "" else sprintf ""","mutation":"%s" """ mutation
    sprintf """{"type":"%s","plantedAt":0,"stage":0,"watered":false,"quality":"%s"%s,"harvestCount":0,"daysWithoutWater":0}""" cropType quality mutation

let private pack (id: string) (dependencies: string) =
    sprintf """{"enabled":true,"pack":{"manifest":{"id":"%s","name":"Pack","version":"1.0.0","dependencies":[%s]},"content":{},"plugins":[]}}""" id dependencies

// ── The cases ────────────────────────────────────────────────────────────────

/// `SchemaValidation.ValidateProject`: every parse-level check.
let private parseCases =
    [ case "schemaVersion not an integer" [ Parse "schemaVersion: Expected integer, received 8.5" ] [ set "schemaVersion" "8.5" ]
      nullCase "mode missing" "mode: Expected string, received null" [ set "mode" "null" ]
      case "mode unknown" [ Parse "mode: Invalid enum value. Expected 'tiles' | 'npcs' | 'items' | 'events' | 'play' | 'quests', received 'bogus'" ] [ set "mode" "\"bogus\"" ]
      case "selectedTileType unknown" [ Parse "selectedTileType: Invalid enum value." ] [ set "selectedTileType" "\"lava\"" ]
      case "currentYear not an integer" [ Parse "currentYear: Expected integer, received 1.5" ] [ set "currentYear" "1.5" ]
      case "scene width not an integer" [ Parse "scenes.0.width: Expected integer, received 16.5" ] [ set "scenes.0.width" "16.5" ]
      case "scene width zero" [ Parse "scenes.0.width: Number must be greater than 0" ] [ set "scenes.0.width" "0" ]
      case "scene height negative" [ Parse "scenes.0.height: Number must be greater than 0" ] [ set "scenes.0.height" "-1" ]
      case "scene height fractional" [ Parse "scenes.0.height: Expected integer, received 1.5" ] [ set "scenes.0.height" "1.5" ]
      nullCase "null tile row" "scenes.0.tiles.3: Expected array, received null" [ set "scenes.0.tiles.3" "null" ]
      nullCase "null tile" "scenes.0.tiles.2.5: Expected object, received null" [ set "scenes.0.tiles.2.5" "null" ]
      case "tile type unknown" [ Parse "scenes.0.tiles.1.1.type: Invalid enum value." ] [ set "scenes.0.tiles.1.1.type" "\"lava\"" ]
      nullCase "tile type missing" "scenes.0.tiles.1.1.type: Expected string, received null" [ set "scenes.0.tiles.1.1.type" "null" ]
      case "tile background unknown" [ Parse "scenes.0.tiles.1.2.background: Invalid enum value." ] [ set "scenes.0.tiles.1.2.background" "\"lava\"" ]
      nullCase "tile background missing" "scenes.0.tiles.1.2.background: Expected string, received null" [ set "scenes.0.tiles.1.2.background" "null" ]
      case "tile overlay unknown" [ Parse "scenes.0.tiles.1.3.overlay: Invalid enum value." ] [ set "scenes.0.tiles.1.3.overlay" "\"lava\"" ]
      case "tile object unknown" [ Parse "scenes.0.tiles.1.4.object: Invalid enum value." ] [ set "scenes.0.tiles.1.4.object" "\"lava\"" ]
      case "tile soilState unknown" [ Parse "scenes.0.tiles.1.5.soilState: Invalid enum value. Expected 'dry' | 'watered' | 'fertilized' | 'tilled', received 'mud'" ] [ set "scenes.0.tiles.1.5.soilState" "\"mud\"" ]
      case "crop quality and mutation unknown"
          [ Parse "scenes.0.tiles.2.2.crop.quality: Invalid enum value."; Parse "scenes.0.tiles.2.2.crop.mutation: Invalid enum value." ]
          [ set "scenes.0.tiles.2.2.crop" (crop "wheat" "legendary" "weird") ]
      nullCase "crop quality missing" "scenes.0.tiles.2.2.crop.quality: Expected string, received null" [ set "scenes.0.tiles.2.2.crop" (crop "wheat" "normal" ""); set "scenes.0.tiles.2.2.crop.quality" "null" ]
      case "player direction unknown" [ Parse "player.direction: Invalid enum value. Expected 'up' | 'down' | 'left' | 'right', received 'north'" ] [ set "player.direction" "\"north\"" ]
      case "item type unknown" [ Parse "items.0.type: Invalid enum value." ] [ set "items.0.type" "\"weapon\"" ]
      case "item toolType unknown" [ Parse "items.0.toolType: Invalid enum value." ] [ set "items.0.toolType" "\"laser\"" ]
      case "item toolTier zero" [ Parse "items.0.toolTier: Number must be greater than 0" ] [ set "items.0.toolTier" "0" ]
      case "item toolTier fractional" [ Parse "items.1.toolTier: Expected integer, received 1.5" ] [ set "items.1.toolTier" "1.5" ]
      case "npc movePattern unknown" [ Parse "npcs.0.movePattern: Invalid enum value." ] [ set "npcs.0.movePattern" "\"fly\"" ]
      case "npc wanderRadius zero" [ Parse "npcs.0.wanderRadius: Number must be greater than 0" ] [ set "npcs.0.wanderRadius" "0" ]
      case "npc patrol point fractional"
          [ Parse "npcs.0.patrolPoints.1.x: Expected integer, received 1.5"; Parse "npcs.0.patrolPoints.1.y: Expected integer, received 2.25" ]
          [ set "npcs.0.patrolPoints" """[{"x":1,"y":1},{"x":1.5,"y":2.25}]""" ]
      case "npc birthday"
          [ Parse "npcs.1.birthday.season: Invalid enum value. Expected 'spring' | 'summer' | 'fall' | 'winter', received 'monsoon'"
            Parse "npcs.1.birthday.day: Expected integer, received 3.5" ]
          [ set "npcs.1.birthday" """{"season":"monsoon","day":3.5}""" ]
      nullCase "npc birthday season missing" "npcs.0.birthday.season: Expected string, received null" [ set "npcs.0.birthday" """{"season":null,"day":3}""" ]
      case "quest status unknown" [ Parse "quests.0.status: Invalid enum value." ] [ set "quests.0.status" "\"maybe\"" ]
      case "quest objective type unknown" [ Parse "quests.0.objectives.0.type: Invalid enum value." ] [ set "quests.0.objectives.0.type" "\"dance\"" ]
      case "event trigger unknown" [ Parse "events.0.trigger: Invalid enum value. Expected 'enter' | 'interact' | 'tick', received 'sometimes'" ]
          [ addEvent "" ""; set "events.0.trigger" "\"sometimes\"" ]
      nullCase "event condition null" "events.0.conditions.0: Expected object, received null" [ addEvent "null" "" ]
      case "inventorySpace quantity"
          [ Parse "events.0.conditions.0.quantity: Expected integer, received 0.5"; Parse "events.0.conditions.1.quantity: Number must be greater than 0" ]
          [ addEvent """{"type":"inventorySpace","itemId":"crop-wheat","quantity":0.5},{"type":"inventorySpace","itemId":"crop-wheat","quantity":0}""" "" ]
      case "questStatus status unknown" [ Parse "events.0.conditions.0.status: Invalid enum value." ]
          [ addEvent """{"type":"questStatus","questId":"quest-first-harvest","status":"zzz"}""" "" ]
      nullCase "event outcome null" "events.0.outcomes.0: Expected object, received null" [ addEvent "" "null" ]
      case "event outcome type unknown" [ Parse "events.0.outcomes.0.type: Invalid enum value." ] [ addEvent "" """{"type":"explode"}""" ]
      nullCase "event outcome type missing" "events.0.outcomes.0.type: Expected string, received null" [ addEvent "" """{"type":null}""" ]
      case "waterArea radius"
          [ Parse "events.0.outcomes.0.radius: Number must be between 0 and 10"
            Parse "events.0.outcomes.1.radius: Expected integer, received 2.5"
            Parse "events.0.outcomes.2.radius: Number must be between 0 and 10" ]
          [ addEvent "" """{"type":"waterArea","radius":11},{"type":"waterArea","radius":2.5},{"type":"waterArea","radius":-1}""" ]
      case "changeTile newTileType unknown" [ Parse "events.0.outcomes.0.newTileType: Invalid enum value." ]
          [ addEvent "" """{"type":"changeTile","tileX":1,"tileY":1,"newTileType":"lava"}""" ]
      { case "outcome amount not finite"
            [ Parse "events.0.outcomes.1.amount: Number must be finite"; Parse "events.0.outcomes.2.amount: Number must be finite" ]
            [ addEvent "" """{"type":"giveMoney","amount":5}""" ]
          with
          Typed =
              fun project ->
                  let event = project.Events.Head
                  let outcomes =
                      event.Outcomes
                      @ [ { EventOutcome.Default with Type = EventOutcomeTypes.GiveMoney; Amount = Some Double.NaN }
                          { EventOutcome.Default with Type = EventOutcomeTypes.TakeMoney; Amount = Some Double.PositiveInfinity } ]
                  { project with Events = { event with Outcomes = outcomes } :: project.Events.Tail } }
      case "action energyCost negative" [ Parse "actions.0.energyCost: Number must be greater than or equal to 0" ] [ set "actions.0.energyCost" "-1" ]
      case "action hotkey too long" [ Parse "actions.0.hotkey: String must contain at most 1 character(s)" ] [ set "actions.0.hotkey" "\"ab\"" ]
      nullCase "action condition null" "actions.1.conditions.0: Expected object, received null" [ set "actions.1.conditions" "[null]" ]
      case "action outcome type unknown" [ Parse "actions.1.outcomes.0.type: Invalid enum value." ] [ set "actions.1.outcomes" """[{"type":"explode"}]""" ]
      case "minigame result tier"
          [ Parse "minigames.0.resultTiers.0.minScore: Number must be between 0 and 1"; Parse "minigames.0.resultTiers.0.outcomes.0.type: Invalid enum value." ]
          [ set "minigames.0.resultTiers" """[{"minScore":1.5,"outcomes":[{"type":"bogus"}]}]""" ]
      case "node type numbers"
          [ Parse "nodeTypes.0.health: Number must be greater than 0"
            Parse "nodeTypes.0.requiredToolTier: Expected integer, received 1.5"
            Parse "nodeTypes.0.respawnDays: Number must be greater than 0" ]
          [ set "nodeTypes.0.health" "0"; set "nodeTypes.0.requiredToolTier" "1.5"; set "nodeTypes.0.respawnDays" "0" ]
      case "node type health fractional" [ Parse "nodeTypes.1.health: Expected integer, received 2.5" ] [ set "nodeTypes.1.health" "2.5" ]
      case "node type requiredTool" [ Parse "nodeTypes.0.requiredTool: Invalid enum value." ] [ set "nodeTypes.0.requiredTool" "\"spoon\"" ]
      nullCase "node type requiredTool missing" "nodeTypes.1.requiredTool: Expected string, received null" [ set "nodeTypes.1.requiredTool" "null" ]
      case "recipe numbers"
          [ Parse "recipes.0.processingMinutes: Number must be greater than or equal to 0"
            Parse "recipes.0.inputs.0.quantity: Number must be greater than 0"
            Parse "recipes.0.outputs.0.quantity: Expected integer, received 1.5" ]
          [ set "recipes.0.processingMinutes" "-5"; set "recipes.0.inputs.0.quantity" "0"; set "recipes.0.outputs.0.quantity" "1.5" ]
      case "weather numbers"
          [ Parse "weather.types.0.cropDamageChance: Number must be between 0 and 1"; Parse "weather.table.spring.0.weight: Number must be greater than 0" ]
          [ set "weather.types.0.cropDamageChance" "2"; set "weather.table.spring.0.weight" "0" ]
      case "settings numbers"
          [ Parse "settings.maxEnergy: Number must be greater than 0"
            Parse "settings.collapseEnergyFraction: Number must be between 0 and 1"
            Parse "settings.collapseMoneyPenalty: Number must be greater than or equal to 0"
            Parse "settings.movement.playerSpeed: Number must be greater than 0"
            Parse "settings.time.dayStartMinute: Expected integer, received 360.5"
            Parse "settings.time.dayEndMinute: Expected integer, received 1560.25"
            Parse "settings.time.minutesPerRealSecond: Number must be greater than 0" ]
          [ set "settings.maxEnergy" "0"
            set "settings.collapseEnergyFraction" "1.5"
            set "settings.collapseMoneyPenalty" "-1"
            set "settings.movement.playerSpeed" "0"
            set "settings.time.dayStartMinute" "360.5"
            set "settings.time.dayEndMinute" "1560.25"
            set "settings.time.minutesPerRealSecond" "0" ]
      case "calendar numbers"
          [ Parse "settings.calendar.seasons.0.days: Number must be greater than 0"; Parse "settings.calendar.festivals.0.day: Expected integer, received 2.5" ]
          [ set "settings.calendar.seasons.0.days" "0"; add "settings.calendar.festivals" """{"id":"fair","name":"Fair","season":"spring","day":2.5}""" ]
      case "mine numbers"
          [ Parse "mine.floors: Number must be greater than 0"
            Parse "mine.floorWidth: Expected integer, received 1.5"
            Parse "mine.floorHeight: Number must be greater than 0"
            Parse "mine.elevatorEvery: Number must be greater than 0"
            Parse "mine.ladderChance: Number must be between 0 and 1"
            Parse "mine.bands.0.fromFloor: Number must be greater than 0"
            Parse "mine.bands.0.toFloor: Expected integer, received 1.5"
            Parse "mine.bands.0.density: Number must be between 0 and 1" ]
          [ set "mine.floors" "0"
            set "mine.floorWidth" "1.5"
            set "mine.floorHeight" "-2"
            set "mine.elevatorEvery" "0"
            set "mine.ladderChance" "2"
            add "mine.bands" """{"fromFloor":0,"toFloor":1.5,"rocks":[],"density":3}""" ]
      case "mineDeepestFloor"
          [ Parse "mineDeepestFloor: Expected integer, received -1.5"; Parse "mineDeepestFloor: Number must be greater than or equal to 0" ]
          [ set "mineDeepestFloor" "-1.5" ]
      case "pack id invalid" [ Parse "contentPacks.0.pack.manifest.id: pack ids must be lowercase letters, digits and dashes" ]
          [ add "contentPacks" (pack "Bad Pack!" "") ]
      case "rngState"
          [ Parse "rngState.algorithm: Invalid literal value, expected \"xoshiro128ss\""; Parse "rngState.s: Expected a tuple of 4 integers" ]
          [ set "rngState" """{"algorithm":"mt19937","s":[1,2,3]}""" ]
      nullCase "rngState words missing" "rngState.s: Expected array, received null" [ set "rngState" """{"algorithm":"xoshiro128ss","s":null}""" ]
      case "rngState algorithm missing" [ Parse "rngState.algorithm: Invalid literal value" ] [ set "rngState" """{"algorithm":null,"s":[1,2,3,4]}""" ] ]

/// `SchemaValidation.LintProject`: every lint-level check.
let private lintCases =
    [ case "project id empty" [ Lint "id: Required id must be a non-empty string" ] [ set "id" "\"\"" ]
      case "scene id empty" [ Lint "scenes.0.id: Required id must be a non-empty string" ] [ set "scenes.0.id" "\"\"" ]
      case "duplicate scene id" [ Lint "scenes.1.id: Duplicate scene id 'scene-farm'" ] [ add "scenes" (scene "scene-farm" "Copy" "") ]
      case "tile rows vs height" [ Lint "scenes.0.tiles: Expected 12 rows (scene height), found 11" ] [ remove "scenes.0.tiles.11" ]
      case "tile row vs width" [ Lint "scenes.0.tiles.4: Expected 16 tiles (scene width), found 15" ] [ remove "scenes.0.tiles.4.15" ]
      case "transition target empty" [ Lint "scenes.0.transitions.0.toSceneId: Required id must be a non-empty string" ]
          [ add "scenes.0.transitions" """{"fromX":0,"fromY":0,"toSceneId":"","toX":0,"toY":0}""" ]
      case "start scene missing" [ Lint "startSceneId: No scene with id 'nowhere'"; Content "Start scene \"nowhere\" does not exist" ] [ set "startSceneId" "\"nowhere\"" ]
      case "player sceneId empty" [ Lint "player.sceneId: Required id must be a non-empty string" ] [ set "player.sceneId" "\"\"" ]
      case "inventory item id empty" [ Lint "player.inventory.0.item.id: Required id must be a non-empty string" ] [ set "player.inventory.0.item.id" "\"\"" ]
      case "item id empty" [ Lint "items.0.id: Required id must be a non-empty string" ] [ set "items.0.id" "\"\"" ]
      case "npc ids empty" [ Lint "npcs.0.id: Required"; Lint "npcs.0.dialogue.0.id: Required" ] [ set "npcs.0.id" "\"\""; set "npcs.0.dialogue.0.id" "\"\"" ]
      case "dialogue id empty" [ Lint "dialogues.0.id: Required id must be a non-empty string" ] [ set "dialogues.0.id" "\"\"" ]
      case "quest ids empty" [ Lint "quests.0.id: Required"; Lint "quests.0.objectives.0.id: Required" ] [ set "quests.0.id" "\"\""; set "quests.0.objectives.0.id" "\"\"" ]
      case "event id empty" [ Lint "events.0.id: Required id must be a non-empty string" ] [ addEvent "" ""; set "events.0.id" "\"\"" ]
      case "region conditions inverted"
          [ Lint "events.0.conditions.0.x2: x2 must be >= x"
            Lint "events.0.conditions.0.y2: y2 must be >= y"
            Lint "events.0.conditions.1.x2: x2 must be >= x"
            Lint "events.0.conditions.1.y2: y2 must be >= y" ]
          [ addEvent """{"type":"enterTile","x":5,"y":5,"x2":2,"y2":1},{"type":"interactTile","x":5,"y":5,"x2":4,"y2":4}""" "" ]
      case "condition ids empty"
          [ Lint "events.0.conditions.0.itemId: Required"
            Lint "events.0.conditions.1.itemId: Required"
            Lint "events.0.conditions.2.flag: Flag name must be a non-empty string"
            Lint "events.0.conditions.3.questId: Required"
            Lint "events.0.conditions.4.npcId: Required"
            Lint "events.0.conditions.5.festivalId: Required" ]
          [ addEvent
                """{"type":"hasItem","itemId":"","quantity":1},{"type":"inventorySpace","itemId":"","quantity":1},{"type":"flag","flag":"","value":true},{"type":"questStatus","questId":"","status":"active"},{"type":"friendship","npcId":"","min":1},{"type":"festivalId","festivalId":""}"""
                "" ]
      // The time of day range (800–300) wraps past midnight: not a problem.
      case "ranges inverted"
          [ Lint "events.0.conditions.0: minDay is greater than maxDay"
            Lint "events.0.conditions.1: minYear is greater than maxYear" ]
          [ addEvent
                """{"type":"dayRange","minDay":20,"maxDay":3},{"type":"yearRange","minYear":3,"maxYear":1},{"type":"timeOfDay","minMinute":800,"maxMinute":300},{"type":"season","seasons":["winter"]},{"type":"weather","weatherIds":["rain"]}"""
                "" ]
      case "action and minigame ids empty" [ Lint "actions.0.id: Required"; Lint "minigames.0.id: Required" ] [ set "actions.0.id" "\"\""; set "minigames.0.id" "\"\"" ]
      case "definition ids empty"
          [ Lint "shops.0.id: Required"
            Lint "recipes.0.id: Required"
            Lint "machineTypes.0.id: Required"
            Lint "animalSpecies.0.id: Required"
            Lint "fishTables.0.id: Required"
            Lint "animals.0.id: Required"
            Lint "nodeTypes.0.id: Required"
            Lint "weather.types.0.id: Required" ]
          [ set "shops.0.id" "\"\""
            set "recipes.0.id" "\"\""
            set "machineTypes.0.id" "\"\""
            set "animalSpecies.0.id" "\"\""
            set "fishTables.0.id" "\"\""
            add "animals" """{"id":"","speciesId":"animal-chicken","name":"Hen","sceneId":"scene-farm","x":1,"y":1}"""
            set "nodeTypes.0.id" "\"\""
            set "weather.types.0.id" "\"\"" ]
      case "calendar ids empty" [ Lint "settings.calendar.seasons.0.id: Required"; Lint "settings.calendar.festivals.0.id: Required" ]
          [ set "settings.calendar.seasons.0.id" "\"\""; add "settings.calendar.festivals" """{"id":"","name":"Fair","season":"spring","day":2}""" ] ]

/// `Validation.ValidateProjectContent`: every content lint.
let private contentCases =
    [ case "transition to a missing scene" [ Content "at (0,0) leads to missing scene \"ghost-scene\"" ]
          [ add "scenes.0.transitions" """{"fromX":0,"fromY":0,"toSceneId":"ghost-scene","toX":0,"toY":0}""" ]
      case "transition lands out of bounds" [ Content "lands out of bounds at (99,-1) in \"Farm\"" ]
          [ add "scenes.0.transitions" """{"fromX":1,"fromY":0,"toSceneId":"scene-farm","toX":99,"toY":-1}""" ]
      case "unreachable scenes"
          [ Content "Scene \"Island\" is unreachable (no transition leads to it)"; NoContent "Mine Floor" ]
          [ add "scenes" (scene "scene-island" "Island" ""); add "scenes" (scene "mine-1" "Mine Floor" ",\"generated\":true") ]
      case "dialogue links"
          [ Content "links to missing dialogue \"dlg-ghost\""
            Content "gives missing item \"item-ghost\""
            Content "opens missing shop \"shop-ghost\""
            Content "offers missing quest \"quest-ghost\"" ]
          [ set "dialogues.0.options.0.nextDialogueId" "\"dlg-ghost\""
            set "dialogues.0.options.0.giveItem" "\"item-ghost\""
            set "npcs.0.dialogue.0.options.0.openShopId" "\"shop-ghost\""
            set "npcs.0.dialogue.0.options.0.offerQuestId" "\"quest-ghost\"" ]
      case "npc placement and schedule"
          [ Content "is placed in missing scene \"scene-ghost\""; Content "schedule targets missing scene \"scene-ghost-2\"" ]
          [ set "npcs.0.sceneId" "\"scene-ghost\""; set "npcs.1.schedule" """[{"minute":360,"sceneId":"scene-ghost-2","x":0,"y":0}]""" ]
      case "quest references"
          [ Content "requires missing quest \"quest-ghost\""
            Content "objective targets missing item \"item-ghost\""
            Content "objective targets missing NPC \"npc-ghost\""
            Content "objective targets missing scene \"scene-ghost\""
            Content "objective targets missing crop \"crop-ghost\""
            Content "rewards missing item \"reward-ghost\"" ]
          [ set "quests.0.prerequisites" """["quest-ghost"]"""
            set "quests.0.objectives.0.targetItemId" "\"item-ghost\""
            set "quests.0.objectives.0.targetNPCId" "\"npc-ghost\""
            set "quests.0.objectives.0.targetSceneId" "\"scene-ghost\""
            set "quests.0.objectives.0.targetCropType" "\"crop-ghost\""
            set "quests.0.rewards.items" """[{"itemId":"reward-ghost","quantity":1}]""" ]
      case "event references"
          [ Content "belongs to missing scene \"scene-ghost\""
            Content "checks missing item \"item-ghost\""
            Content "checks missing quest \"quest-ghost\""
            Content "references missing item \"item-ghost-2\""
            Content "references missing quest \"quest-ghost-2\""
            Content "references missing NPC \"npc-ghost\""
            Content "warps to missing scene \"scene-ghost-2\"" ]
          [ addEvent
                """{"type":"hasItem","itemId":"item-ghost","quantity":1},{"type":"questStatus","questId":"quest-ghost","status":"active"}"""
                """{"type":"takeItem","itemId":"item-ghost-2"},{"type":"completeQuest","questId":"quest-ghost-2"},{"type":"startDialogue","npcId":"npc-ghost"},{"type":"warpPlayer","sceneId":"scene-ghost-2","x":0,"y":0},{"type":"giveItem","itemId":""}"""
            set "events.0.sceneId" "\"scene-ghost\"" ]
      case "shop stocks a missing item" [ Content "stocks missing item \"item-ghost\"" ] [ add "shops.0.stock" """{"itemId":"item-ghost"}""" ]
      case "node type drops a missing item" [ Content "drops missing item \"item-ghost\"" ]
          [ add "nodeTypes.0.drops" """{"itemId":"item-ghost","min":1,"max":1,"weight":1}""" ]
      case "placed node of a missing type" [ Content "has a placed node of missing type \"node-ghost\" at (3,2)" ]
          [ set "scenes.0.tiles.2.3.node" """{"typeId":"node-ghost","remainingHealth":1}""" ]
      case "planted crop of a missing type" [ Content "has a planted crop of missing type \"crop-ghost\" at (4,2)" ]
          [ set "scenes.0.tiles.2.4.crop" (crop "crop-ghost" "normal" "") ]
      case "crop out of season" [ Content "Pumpkin at (5,2) cannot grow in the starting season (spring)" ]
          [ set "scenes.0.tiles.2.5.crop" (crop "pumpkin" "gold" "giant") ]
      case "seed of a missing crop" [ Content "references missing crop \"crop-ghost\"" ]
          [ add "items" """{"id":"seed-ghost","name":"Ghost Seeds","description":"","type":"seed","stackable":true,"maxStack":99,"value":1,"cropType":"crop-ghost"}""" ]
      case "pack problems" [ Content "'pack-ghost'" ] [ add "contentPacks" (pack "lonely" """{"packId":"pack-ghost"}""") ] ]

/// Nulls where the schema has none: the typed parse refuses them at the first one.
let private malformedCases =
    [ malformed "scenes null" "scenes: Expected array, received null" [ set "scenes" "null" ]
      malformed "scene transitions null" "scenes.0.transitions: Expected array, received null" [ set "scenes.0.transitions" "null" ]
      malformed "scene tiles null" "scenes.0.tiles: Expected array, received null" [ set "scenes.0.tiles" "null" ]
      malformed "player null" "player: Expected object, received null" [ set "player" "null" ]
      malformed "items null" "items: Expected array, received null" [ set "items" "null" ]
      malformed "item null" "items.3: Expected object, received null" [ set "items.3" "null" ]
      malformed "npc dialogue null" "npcs.0.dialogue: Expected array, received null" [ set "npcs.0.dialogue" "null" ]
      malformed "shops null" "shops: Expected array, received null" [ set "shops" "null" ]
      malformed "animals null" "animals: Expected array, received null" [ set "animals" "null" ]
      malformed "machine type null" "machineTypes.1: Expected object, received null" [ set "machineTypes.1" "null" ]
      malformed "weather table null" "weather.table: Expected object, received null" [ set "weather.table" "null" ]
      malformed "settings movement null" "settings.movement: Expected object, received null" [ set "settings.movement" "null" ]
      malformed "mine bands null" "mine.bands: Expected array, received null" [ set "mine.bands" "null" ]
      malformed "pack manifest id null" "contentPacks.0.pack.manifest.id: Expected string, received null"
          [ add "contentPacks" (pack "p" ""); set "contentPacks.0.pack.manifest.id" "null" ]
      malformed "crop type null" "scenes.0.tiles.2.2.crop.type: Expected string, received null"
          [ set "scenes.0.tiles.2.2.crop" (crop "wheat" "normal" ""); set "scenes.0.tiles.2.2.crop.type" "null" ]
      malformed "quest rewards null" "quests.0.rewards: Expected object, received null" [ set "quests.0.rewards" "null" ]
      malformed "null tile row, content" "scenes.0.tiles.0: Expected array, received null" [ set "scenes.0.tiles.0" "null" ] ]

/// Every case, and one with all of the checkable ones at once (past the 20-error cap).
let cases : Case list =
    let single = parseCases @ lintCases @ contentCases
    // Without the fractional schemaVersion, which the migrations refuse before any check runs,
    // and the nulls, which the typed parse refuses before any check runs.
    let isDecode = function Decode _ -> true | _ -> false
    let combinable =
        single |> List.filter (fun c -> c.Name <> "schemaVersion not an integer" && not (List.exists isDecode c.Expect))
    let combined =
        { Name = "everything at once"
          // Later edits may reach into something an earlier one nulled out; those are skipped.
          Edit =
              fun root ->
                  for c in combinable do
                      try
                          c.Edit root
                      with _ ->
                          ()
          Typed = fun project -> combinable |> List.fold (fun p c -> c.Typed p) project
          Expect = [] }
    single @ malformedCases @ [ combined ]

/// The starter farm as JSON, fresh on each call.
let starterJson () : JsonObject = (ProjectMigrations.toNode (TestProjects.starter ())).AsObject()

/// A case's JSON (the starter with its edits).
let json (case: Case) : JsonObject =
    let root = starterJson ()
    case.Edit root
    root

/// A case's project: its JSON through the typed parse, then its typed edits; or the parse issue.
let tryProject (case: Case) : Result<GameProject, string> =
    Decode.run SchemaJson.decodeGameProject (JsonInterop.ofNode (json case)) |> Result.map case.Typed

/// A case's project (the case must parse).
let project (case: Case) : GameProject =
    match tryProject case with
    | Ok project -> project
    | Error issue -> failwithf "%s does not parse: %s" case.Name issue
