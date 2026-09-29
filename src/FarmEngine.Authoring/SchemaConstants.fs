namespace FarmEngine.Schemas

open FarmEngine.Authoring

// The schema's enums (string constants, as zod string enums stay open strings in JSON) and the
// constants and small helpers of packages/engine-schemas that are not records.

/// TS `TileTypeSchema`.
[<RequireQualifiedAccess>]
module TileTypes =
    [<Literal>]
    let Grass = "grass"

    [<Literal>]
    let Soil = "soil"

    [<Literal>]
    let Water = "water"

    [<Literal>]
    let Path = "path"

    [<Literal>]
    let Wall = "wall"

    [<Literal>]
    let Door = "door"

    [<Literal>]
    let Floor = "floor"

    let All : string list = [ Grass; Soil; Water; Path; Wall; Door; Floor ]

/// TS `DirectionSchema`.
[<RequireQualifiedAccess>]
module Directions =
    [<Literal>]
    let Up = "up"

    [<Literal>]
    let Down = "down"

    [<Literal>]
    let Left = "left"

    [<Literal>]
    let Right = "right"

    let All : string list = [ Up; Down; Left; Right ]

/// TS `ItemTypeSchema`.
[<RequireQualifiedAccess>]
module ItemTypes =
    [<Literal>]
    let Seed = "seed"

    [<Literal>]
    let Tool = "tool"

    [<Literal>]
    let Crop = "crop"

    [<Literal>]
    let Gift = "gift"

    [<Literal>]
    let Quest = "quest"

    [<Literal>]
    let Fertilizer = "fertilizer"

    [<Literal>]
    let Material = "material"

    [<Literal>]
    let Fish = "fish"

    let All : string list = [ Seed; Tool; Crop; Gift; Quest; Fertilizer; Material; Fish ]

/// TS `ToolTypeSchema`.
[<RequireQualifiedAccess>]
module ToolTypes =
    [<Literal>]
    let WateringCan = "watering-can"

    [<Literal>]
    let Hoe = "hoe"

    [<Literal>]
    let Axe = "axe"

    [<Literal>]
    let Pickaxe = "pickaxe"

    [<Literal>]
    let Scythe = "scythe"

    [<Literal>]
    let FishingRod = "fishing-rod"

    let All : string list = [ WateringCan; Hoe; Axe; Pickaxe; Scythe; FishingRod ]

/// TS `EditorModeSchema`.
[<RequireQualifiedAccess>]
module EditorModes =
    [<Literal>]
    let Tiles = "tiles"

    [<Literal>]
    let Npcs = "npcs"

    [<Literal>]
    let Items = "items"

    [<Literal>]
    let Events = "events"

    [<Literal>]
    let Play = "play"

    [<Literal>]
    let Quests = "quests"

    let All : string list = [ Tiles; Npcs; Items; Events; Play; Quests ]

/// TS `QuestStatusSchema`.
[<RequireQualifiedAccess>]
module QuestStatuses =
    [<Literal>]
    let NotStarted = "not-started"

    [<Literal>]
    let Active = "active"

    [<Literal>]
    let Completed = "completed"

    [<Literal>]
    let Failed = "failed"

    let All : string list = [ NotStarted; Active; Completed; Failed ]

/// TS `QuestObjectiveTypeSchema`.
[<RequireQualifiedAccess>]
module QuestObjectiveTypes =
    [<Literal>]
    let Collect = "collect"

    [<Literal>]
    let Harvest = "harvest"

    [<Literal>]
    let Talk = "talk"

    [<Literal>]
    let Visit = "visit"

    [<Literal>]
    let Craft = "craft"

    [<Literal>]
    let Gift = "gift"

    let All : string list = [ Collect; Harvest; Talk; Visit; Craft; Gift ]

/// TS `SoilStateSchema`.
[<RequireQualifiedAccess>]
module SoilStates =
    [<Literal>]
    let Dry = "dry"

    [<Literal>]
    let Watered = "watered"

    [<Literal>]
    let Fertilized = "fertilized"

    [<Literal>]
    let Tilled = "tilled"

    let All : string list = [ Dry; Watered; Fertilized; Tilled ]

/// TS `CropQualitySchema`.
[<RequireQualifiedAccess>]
module CropQualities =
    [<Literal>]
    let Normal = "normal"

    [<Literal>]
    let Silver = "silver"

    [<Literal>]
    let Gold = "gold"

    [<Literal>]
    let Iridium = "iridium"

    let All : string list = [ Normal; Silver; Gold; Iridium ]

/// TS `CropMutationSchema`: one of these or `null` (so C# fields holding it are `string?` written as `null`).
[<RequireQualifiedAccess>]
module CropMutations =
    [<Literal>]
    let Giant = "giant"

    [<Literal>]
    let Golden = "golden"

    [<Literal>]
    let Ancient = "ancient"

    let All : string list = [ Giant; Golden; Ancient ]

/// TS `NPCSchema.movePattern` enum.
[<RequireQualifiedAccess>]
module NpcMovePatterns =
    [<Literal>]
    let Stationary = "stationary"

    [<Literal>]
    let Wander = "wander"

    [<Literal>]
    let Patrol = "patrol"

    let All : string list = [ Stationary; Wander; Patrol ]

/// TS `CustomAssetSchema.type` enum.
[<RequireQualifiedAccess>]
module CustomAssetTypes =
    [<Literal>]
    let Tile = "tile"

    [<Literal>]
    let Npc = "npc"

    [<Literal>]
    let Item = "item"

    [<Literal>]
    let Player = "player"

    [<Literal>]
    let Art = "art"

    let All : string list = [ Tile; Npc; Item; Player; Art ]

/// TS `EventOutcomeSchema.type` enum.
[<RequireQualifiedAccess>]
module EventOutcomeTypes =
    [<Literal>]
    let Message = "message"

    [<Literal>]
    let ModifyFriendship = "modifyFriendship"

    [<Literal>]
    let ModifyEnergy = "modifyEnergy"

    [<Literal>]
    let WaterArea = "waterArea"

    [<Literal>]
    let GiveItem = "giveItem"

    [<Literal>]
    let TakeItem = "takeItem"

    [<Literal>]
    let GiveMoney = "giveMoney"

    [<Literal>]
    let TakeMoney = "takeMoney"

    [<Literal>]
    let SetFlag = "setFlag"

    [<Literal>]
    let ClearFlag = "clearFlag"

    [<Literal>]
    let StartQuest = "startQuest"

    [<Literal>]
    let CompleteQuest = "completeQuest"

    [<Literal>]
    let SpawnNpc = "spawnNPC"

    [<Literal>]
    let RemoveNpc = "removeNPC"

    [<Literal>]
    let ChangeTile = "changeTile"

    [<Literal>]
    let WarpPlayer = "warpPlayer"

    [<Literal>]
    let StartDialogue = "startDialogue"

    [<Literal>]
    let LockTransition = "lockTransition"

    [<Literal>]
    let UnlockTransition = "unlockTransition"

    [<Literal>]
    let PlaySound = "playSound"

    /// Run a creator-defined action (extensibility layer).
    [<Literal>]
    let PerformAction = "performAction"

    /// Open a declared minigame; its score resolves via the command log.
    [<Literal>]
    let StartMinigame = "startMinigame"

    /// legacy (pre-v5), still honored by the migration
    [<Literal>]
    let UnlockScene = "unlockScene"

    let All : string list = [ Message; ModifyFriendship; ModifyEnergy; WaterArea; GiveItem; TakeItem; GiveMoney; TakeMoney; SetFlag; ClearFlag; StartQuest; CompleteQuest; SpawnNpc; RemoveNpc; ChangeTile; WarpPlayer; StartDialogue; LockTransition; UnlockTransition; PlaySound; PerformAction; StartMinigame; UnlockScene ]

/// TS `EventTriggerSchema`.
[<RequireQualifiedAccess>]
module EventTriggers =
    [<Literal>]
    let Enter = "enter"

    [<Literal>]
    let Interact = "interact"

    [<Literal>]
    let Tick = "tick"

    let All : string list = [ Enter; Interact; Tick ]

/// TS `GiftReaction` (`keyof typeof GIFT_FRIENDSHIP_DELTAS`).
[<RequireQualifiedAccess>]
module GiftReactions =
    [<Literal>]
    let Loved = "loved"

    [<Literal>]
    let Liked = "liked"

    [<Literal>]
    let Neutral = "neutral"

    [<Literal>]
    let Disliked = "disliked"

    [<Literal>]
    let Hated = "hated"

    let All : string list = [ Loved; Liked; Neutral; Disliked; Hated ]

/// TS `GamePanelSchema.entries[].kind` enum.
[<RequireQualifiedAccess>]
module GamePanelEntryKinds =
    [<Literal>]
    let Text = "text"

    [<Literal>]
    let Money = "money"

    [<Literal>]
    let Energy = "energy"

    [<Literal>]
    let Day = "day"

    [<Literal>]
    let Item = "item"

    [<Literal>]
    let Flag = "flag"

    [<Literal>]
    let Action = "action"

    let All : string list = [ Text; Money; Energy; Day; Item; Flag; Action ]


/// `primitives.ts` lists.
[<RequireQualifiedAccess>]
module PrimitivesSchema =
    /// Seasons are open strings (creator-defined calendars, M9) so custom season ids validate the
    /// same way custom crops do. `CLASSIC_SEASONS` enumerates the four seasons the built-in
    /// calendar ships with, for defaults/editors.
    let ClassicSeasons : string list = [ "spring"; "summer"; "fall"; "winter" ]

    /// Crop identifiers are open strings so that custom/modded crops share the same pipeline as
    /// built-in ones. `BUILTIN_CROP_IDS` enumerates the crops shipped with the default content pack.
    let BuiltinCropIds : string list =
        [ "wheat"; "corn"; "tomato"; "carrot"; "potato"; "strawberry"; "pumpkin"; "cauliflower"; "blueberry" ]

[<RequireQualifiedAccess>]
module SettingsSchema =
    let ClassicCalendarSeasons () : CalendarSeason list =
        PrimitivesSchema.ClassicSeasons
        |> List.map (fun id -> { Id = id; Name = id.Substring(0, 1).ToUpperInvariant() + id.Substring 1; Days = 28.0 })

    let DefaultProjectSettings : ProjectSettings = ProjectSettings.Default

[<RequireQualifiedAccess>]
module ProjectSchema =
    /// The project schema version. Bump when the persisted shape changes and add a migration plus
    /// a fixture in tests/.
    ///
    /// v1 — original prototype (tiles without layer fields); v2 — layered tiles
    /// (background/overlay/object); v3 — explicit schemaVersion + guaranteed presence of
    /// customAssets, season/day fields, quests and player pixel/quest fields; v4 — day-based game
    /// time (M2): crop growthDays/plantedOnDay/daysGrown, shops, gathering node types, project
    /// settings (energy/time), player energy, currentTimeMinutes; v5 — events v2 (M3): trigger +
    /// combinable conditions + sequenced outcomes, NPC schedules, lockable transitions, quest
    /// availability; v6 — simulation depth (M4): recipes, machine types, weather config, animal
    /// species + animals, fish tables, mine config, gift tastes, player skills; v7 — content packs
    /// (M5): installed packs (with load order + enable flags) travel inside the project; v8 —
    /// project graphics settings (`graphics`, pixel art by default).
    [<Literal>]
    let CurrentProjectSchemaVersion = 8.0

[<RequireQualifiedAccess>]
module GameContentSchema =
    [<Literal>]
    let CurrentContentVersion = 1.0

[<RequireQualifiedAccess>]
module SaveSchema =
    [<Literal>]
    let CurrentSaveVersion = 4.0

    /// TS `SKILL_NAMES`.
    let SkillNames : string list = [ "farming"; "foraging"; "fishing"; "mining"; "social" ]

    /// Tile index → tile center; already-fractional coordinates pass through.
    let CenterCoordinate (value: float) : float = if value = System.Math.Floor value then value + 0.5 else value

[<RequireQualifiedAccess>]
module SocialSchema =
    [<Literal>]
    let FriendshipPerHeart = 125.0

    [<Literal>]
    let MaxFriendship = 1250.0

    let GiftFriendshipDeltas : (string * float) list =
        [ GiftReactions.Loved, 80.0
          GiftReactions.Liked, 45.0
          GiftReactions.Neutral, 20.0
          GiftReactions.Disliked, -20.0
          GiftReactions.Hated, -40.0 ]

[<RequireQualifiedAccess>]
module EventsSchema =
    /// Flag used to record that a non-repeatable event has fired.
    let EventFiredFlag (eventId: string) : string = "event:" + eventId + ":fired"

[<RequireQualifiedAccess>]
module ExtensibilitySchema =
    [<Literal>]
    let FishingMinigameId = "fishing"

[<RequireQualifiedAccess>]
module MigrationsSchema =
    let private entry (weatherId: string) (weight: float) : WeatherTableEntry = { WeatherId = weatherId; Weight = weight }

    let private weather (id: string) (name: string) (waters: bool) (damage: float) (inside: bool) (overlay: string option) : WeatherTypeDefinition =
        { Id = id; Name = name; WatersOutdoorSoil = waters; CropDamageChance = damage; NpcsStayInside = inside; Overlay = overlay; Extra = [] }

    /// Default weather set every pre-v6 project receives (tunable afterwards).
    let DefaultWeatherConfig () : WeatherConfig =
        { Types =
            [ weather "sun" "Sunny" false 0.0 false None
              weather "rain" "Rain" true 0.0 false (Some "rain")
              weather "storm" "Storm" true 0.03 true (Some "rain")
              weather "snow" "Snow" false 0.0 false (Some "snow") ]
          Table =
            [ "spring", [ entry "sun" 6.0; entry "rain" 3.0; entry "storm" 1.0 ]
              "summer", [ entry "sun" 7.0; entry "rain" 1.0; entry "storm" 2.0 ]
              "fall", [ entry "sun" 6.0; entry "rain" 3.0; entry "storm" 1.0 ]
              "winter", [ entry "sun" 5.0; entry "snow" 5.0 ] ]
          Extra = [] }
