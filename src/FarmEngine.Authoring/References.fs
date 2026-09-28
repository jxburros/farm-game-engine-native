namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open FarmEngine.Schemas

/// What an id in a project names. Pickers list the ids of one kind (`References.options`).
[<RequireQualifiedAccess>]
type ReferenceKind =
    | Item
    | Npc
    | Scene
    | Quest
    | Dialogue
    | Shop
    | Recipe
    | Crop
    | NodeType
    | MachineType
    | AnimalSpecies
    | Animal
    | FishTable
    | Action
    | Minigame
    | Event
    | Weather
    | Season
    | Festival
    | Skill
    | Asset
    | Pack
    /// A crafting station category that machine types provide (`stationCategories`).
    | StationCategory

module ReferenceKind =
    let all =
        [ ReferenceKind.Item; ReferenceKind.Npc; ReferenceKind.Scene; ReferenceKind.Quest; ReferenceKind.Dialogue
          ReferenceKind.Shop; ReferenceKind.Recipe; ReferenceKind.Crop; ReferenceKind.NodeType; ReferenceKind.MachineType
          ReferenceKind.AnimalSpecies; ReferenceKind.Animal; ReferenceKind.FishTable; ReferenceKind.Action
          ReferenceKind.Minigame; ReferenceKind.Event; ReferenceKind.Weather; ReferenceKind.Season; ReferenceKind.Festival
          ReferenceKind.Skill; ReferenceKind.Asset; ReferenceKind.Pack; ReferenceKind.StationCategory ]

    /// The name C# uses ("item", "npc", "nodeType", …).
    let name (kind: ReferenceKind) =
        match kind with
        | ReferenceKind.Item -> "item"
        | ReferenceKind.Npc -> "npc"
        | ReferenceKind.Scene -> "scene"
        | ReferenceKind.Quest -> "quest"
        | ReferenceKind.Dialogue -> "dialogue"
        | ReferenceKind.Shop -> "shop"
        | ReferenceKind.Recipe -> "recipe"
        | ReferenceKind.Crop -> "crop"
        | ReferenceKind.NodeType -> "nodeType"
        | ReferenceKind.MachineType -> "machineType"
        | ReferenceKind.AnimalSpecies -> "animalSpecies"
        | ReferenceKind.Animal -> "animal"
        | ReferenceKind.FishTable -> "fishTable"
        | ReferenceKind.Action -> "action"
        | ReferenceKind.Minigame -> "minigame"
        | ReferenceKind.Event -> "event"
        | ReferenceKind.Weather -> "weather"
        | ReferenceKind.Season -> "season"
        | ReferenceKind.Festival -> "festival"
        | ReferenceKind.Skill -> "skill"
        | ReferenceKind.Asset -> "asset"
        | ReferenceKind.Pack -> "pack"
        | ReferenceKind.StationCategory -> "stationCategory"

    let tryParse (text: string) = all |> List.tryFind (fun kind -> name kind = text)

/// One entry of a picker: the id to store and the text to show. `Missing` marks the entry that
/// keeps a stored id the project no longer has, so the creator sees it instead of losing it.
type PickerOption =
    { Id: string
      Label: string
      Missing: bool }

/// What a schema property holds (docs/LANGUAGES.md: the editor asks F# what a field means).
[<RequireQualifiedAccess>]
type FieldRole =
    /// The id of one thing. `empty` labels the "nothing chosen" entry when empty is allowed.
    | Reference of kind: ReferenceKind * empty: string option
    /// A list of ids.
    | ReferenceList of kind: ReferenceKind
    /// A dictionary keyed by ids (weather table rows by season, social state by NPC).
    | ReferenceKeys of kind: ReferenceKind
    /// One of fixed values, as (value, label) pairs.
    | OneOf of choices: (string * string) list * empty: string option
    /// Named like a reference but deliberately not one: the reason says what it holds.
    | NotReference of reason: string

/// A property of a C# schema record (`Owner` is the record type name, `Property` the C#
/// property name) and what it holds.
type FieldDeclaration =
    { Owner: string
      Property: string
      Role: FieldRole }

/// Every schema property that holds an id of another thing, and the enum-like strings the editor
/// shows as choices. The `Id` property of a record is its own id and is never declared. A test
/// walks the C# records reachable from `GameProject` and fails when an id-like property has no
/// entry here, so a new reference field cannot slip into the schema without a picker.
module References =
    let private none = Some "(none)"
    let private reference owner property kind = { Owner = owner; Property = property; Role = FieldRole.Reference(kind, None) }
    let private optional owner property kind = { Owner = owner; Property = property; Role = FieldRole.Reference(kind, none) }
    let private optionalAs owner property kind empty = { Owner = owner; Property = property; Role = FieldRole.Reference(kind, Some empty) }
    let private list owner property kind = { Owner = owner; Property = property; Role = FieldRole.ReferenceList kind }
    let private keys owner property kind = { Owner = owner; Property = property; Role = FieldRole.ReferenceKeys kind }
    let private plain owner property reason = { Owner = owner; Property = property; Role = FieldRole.NotReference reason }
    let private same (values: seq<string>) = values |> Seq.map (fun v -> v, v) |> List.ofSeq
    let private oneOf owner property (choices: (string * string) list) = { Owner = owner; Property = property; Role = FieldRole.OneOf(choices, None) }
    let private optionalOneOf owner property (choices: (string * string) list) = { Owner = owner; Property = property; Role = FieldRole.OneOf(choices, none) }

    /// NPCEditor appearance select.
    let appearances =
        [ "villager", "Villager"; "farmer", "Farmer"; "merchant", "Merchant"; "elder", "Elder"; "child", "Child"; "guard", "Guard" ]

    /// QuestEditor objective type select (plus `gift`, which the engine also tracks).
    let objectiveTypes =
        [ QuestObjectiveTypes.Collect, "Collect Item"; QuestObjectiveTypes.Harvest, "Harvest Crop"
          QuestObjectiveTypes.Talk, "Talk to NPC"; QuestObjectiveTypes.Visit, "Visit Scene"
          QuestObjectiveTypes.Craft, "Craft Item"; QuestObjectiveTypes.Gift, "Give Gift" ]

    let private tileTypes = same TileTypes.All
    let private toolTypes = same ToolTypes.All
    let private flag = "a flag name: flags are free text that events, actions and dialogue set and check"

    let declarations: FieldDeclaration list =
        [ // Project
          reference "GameProject" "StartSceneId" ReferenceKind.Scene
          oneOf "GameProject" "Mode" (same EditorModes.All)
          oneOf "GameProject" "SelectedTileType" tileTypes
          optional "GameProject" "SelectedNpcId" ReferenceKind.Npc
          optional "GameProject" "SelectedItemId" ReferenceKind.Item
          plain "GameProject" "PlayerCustomImage" "legacy player image: an asset id or a data URL"
          reference "GameProject" "CurrentSeason" ReferenceKind.Season
          optional "GameProject" "CurrentWeatherId" ReferenceKind.Weather
          plain "GameProject" "EventFlags" flag
          keys "GameProject" "SocialState" ReferenceKind.Npc
          // Player start
          oneOf "Player" "Direction" (same Directions.All)
          reference "Player" "SceneId" ReferenceKind.Scene
          list "Player" "ActiveQuests" ReferenceKind.Quest
          list "Player" "CompletedQuests" ReferenceKind.Quest
          optionalOneOf "Player" "EquippedTool" toolTypes
          keys "Player" "Skills" ReferenceKind.Skill
          // World
          list "Scene" "Npcs" ReferenceKind.Npc
          list "Scene" "Events" ReferenceKind.Event
          reference "SceneTransition" "ToSceneId" ReferenceKind.Scene
          oneOf "Tile" "Type" tileTypes
          oneOf "Tile" "Background" tileTypes
          optionalOneOf "Tile" "Overlay" tileTypes
          optionalOneOf "Tile" "Object" tileTypes
          plain "Tile" "CustomImage" "legacy tile image: an asset id or a data URL"
          optionalOneOf "Tile" "SoilState" (same SoilStates.All)
          reference "Crop" "Type" ReferenceKind.Crop
          oneOf "Crop" "Quality" (same CropQualities.All)
          optionalOneOf "Crop" "Mutation" (same CropMutations.All)
          plain "Crop" "MultiTileId" "groups the tiles of one planted multi-tile crop"
          reference "TileNode" "TypeId" ReferenceKind.NodeType
          reference "TileMachine" "TypeId" ReferenceKind.MachineType
          reference "MachineProcessing" "RecipeId" ReferenceKind.Recipe
          // Art
          reference "VisualRef" "AssetId" ReferenceKind.Asset
          plain "VisualRef" "Animation" "a clip name on the chosen asset"
          optionalAs "ArtFrame" "AssetId" ReferenceKind.Asset "(same image)"
          oneOf "CustomAsset" "Type" (same CustomAssetTypes.All)
          optionalOneOf "CustomAsset" "TileType" tileTypes
          // Items and crops
          oneOf "Item" "Type" (same ItemTypes.All)
          optional "Item" "CropType" ReferenceKind.Crop
          plain "Item" "CustomImage" "legacy item image: an asset id or a data URL"
          optionalOneOf "Item" "ToolType" toolTypes
          optional "Item" "UseActionId" ReferenceKind.Action
          list "CropDefinition" "Seasons" ReferenceKind.Season
          list "CustomCropDefinition" "Seasons" ReferenceKind.Season
          plain "CustomCropDefinition" "CustomAsset" "legacy crop image as a data URL"
          // Characters and dialogue
          reference "Npc" "SceneId" ReferenceKind.Scene
          optionalOneOf "Npc" "MovePattern" (same NpcMovePatterns.All)
          oneOf "Npc" "Appearance" appearances
          plain "Npc" "CustomImage" "legacy NPC image: an asset id or a data URL"
          oneOf "NpcBirthday" "Season" (same PrimitivesSchema.ClassicSeasons)
          reference "NpcScheduleEntry" "SceneId" ReferenceKind.Scene
          list "GiftTastes" "Loved" ReferenceKind.Item
          list "GiftTastes" "Liked" ReferenceKind.Item
          list "GiftTastes" "Disliked" ReferenceKind.Item
          list "GiftTastes" "Hated" ReferenceKind.Item
          reference "Dialogue" "NpcId" ReferenceKind.Npc
          optionalAs "DialogueOption" "NextDialogueId" ReferenceKind.Dialogue "(end conversation)"
          optional "DialogueOption" "GiveItem" ReferenceKind.Item
          plain "DialogueOption" "EventFlag" flag
          optional "DialogueOption" "RequiresItem" ReferenceKind.Item
          plain "DialogueOption" "RequiresFlag" flag
          optional "DialogueOption" "OpenShopId" ReferenceKind.Shop
          optional "DialogueOption" "OfferQuestId" ReferenceKind.Quest
          optional "DialogueOption" "ActionId" ReferenceKind.Action
          // Quests
          optional "Quest" "Giver" ReferenceKind.Npc
          oneOf "Quest" "Status" (same QuestStatuses.All)
          list "Quest" "Prerequisites" ReferenceKind.Quest
          list "Quest" "AvailableSeasons" ReferenceKind.Season
          oneOf "QuestObjective" "Type" objectiveTypes
          optional "QuestObjective" "TargetItemId" ReferenceKind.Item
          optional "QuestObjective" "TargetCropType" ReferenceKind.Crop
          optional "QuestObjective" "TargetNpcId" ReferenceKind.Npc
          optional "QuestObjective" "TargetSceneId" ReferenceKind.Scene
          reference "QuestRewardItem" "ItemId" ReferenceKind.Item
          // Events and the condition/outcome vocabulary
          optionalAs "GameEvent" "SceneId" ReferenceKind.Scene "(every scene)"
          oneOf "GameEvent" "Trigger" (same EventTriggers.All)
          reference "HasItemCondition" "ItemId" ReferenceKind.Item
          reference "InventorySpaceCondition" "ItemId" ReferenceKind.Item
          plain "FlagCondition" "Flag" flag
          list "SeasonCondition" "Seasons" ReferenceKind.Season
          reference "QuestStatusCondition" "QuestId" ReferenceKind.Quest
          oneOf "QuestStatusCondition" "Status" (same QuestStatuses.All)
          reference "FriendshipCondition" "NpcId" ReferenceKind.Npc
          list "WeatherCondition" "WeatherIds" ReferenceKind.Weather
          reference "FestivalIdCondition" "FestivalId" ReferenceKind.Festival
          oneOf "EventOutcome" "Type" (same EventOutcomeTypes.All)
          optional "EventOutcome" "ItemId" ReferenceKind.Item
          plain "EventOutcome" "FlagName" flag
          optional "EventOutcome" "QuestId" ReferenceKind.Quest
          optional "EventOutcome" "NpcId" ReferenceKind.Npc
          optional "EventOutcome" "DialogueId" ReferenceKind.Dialogue
          optionalOneOf "EventOutcome" "NewTileType" tileTypes
          optional "EventOutcome" "SceneId" ReferenceKind.Scene
          plain "EventOutcome" "SoundId" "a sound name the host plays; sounds are not project content yet"
          optional "EventOutcome" "ActionId" ReferenceKind.Action
          optional "EventOutcome" "MinigameId" ReferenceKind.Minigame
          // Economy, crafting, gathering
          reference "ShopStockEntry" "ItemId" ReferenceKind.Item
          list "ShopStockEntry" "Seasons" ReferenceKind.Season
          reference "RecipeIngredient" "ItemId" ReferenceKind.Item
          optionalAs "RecipeDefinition" "MachineTypeId" ReferenceKind.MachineType "(by hand)"
          optionalAs "RecipeDefinition" "RequiresStationCategory" ReferenceKind.StationCategory "(anywhere)"
          reference "RecipeSkillRequirement" "Skill" ReferenceKind.Skill
          optional "RecipeUnlock" "QuestId" ReferenceKind.Quest
          list "RecipeUnlock" "Seasons" ReferenceKind.Season
          optional "MachineTypeDefinition" "ItemId" ReferenceKind.Item
          plain "MachineTypeDefinition" "StationCategories" "the station categories this machine provides (it defines them)"
          oneOf "NodeTypeDefinition" "RequiredTool" toolTypes
          reference "NodeDrop" "ItemId" ReferenceKind.Item
          // Wildlife
          optionalAs "AnimalSpeciesDefinition" "FeedItemId" ReferenceKind.Item "(grazes for free)"
          reference "AnimalSpeciesDefinition" "ProductItemId" ReferenceKind.Item
          reference "AnimalState" "SpeciesId" ReferenceKind.AnimalSpecies
          reference "AnimalState" "SceneId" ReferenceKind.Scene
          list "FishTable" "Seasons" ReferenceKind.Season
          list "FishTable" "SceneIds" ReferenceKind.Scene
          reference "FishTableEntry" "ItemId" ReferenceKind.Item
          optional "FishTable" "JunkItemId" ReferenceKind.Item
          // Extensibility
          plain "MinigameDef" "Config" "settings passed to the minigame kind, keyed by setting name"
          // Settings, weather, mine, interface
          plain "ProjectSettings" "Locale" "a locale code for pack string tables"
          reference "CalendarFestival" "SeasonId" ReferenceKind.Season
          optionalOneOf "WeatherTypeDefinition" "Overlay" [ "rain", "rain"; "snow", "snow" ]
          keys "WeatherConfig" "Table" ReferenceKind.Season
          reference "WeatherTableEntry" "WeatherId" ReferenceKind.Weather
          optional "MineConfig" "EntranceSceneId" ReferenceKind.Scene
          reference "MineRockWeight" "NodeTypeId" ReferenceKind.NodeType
          plain "GamePanel" "VisibleFlag" flag
          oneOf "GamePanelEntry" "Kind" (same GamePanelEntryKinds.All)
          plain "GamePanelEntry" "Value" "text, an item id, an action id or a flag, depending on the entry kind"
          // Content packs
          reference "PackDependency" "PackId" ReferenceKind.Pack
          plain "PackManifest" "Overrides" "ids from other packs this pack replaces (checked when packs merge)"
          plain "PackPermissions" "Hooks" "plugin hook names the pack may use"
          plain "PackPlugin" "Hooks" "plugin hook names the plugin handles"
          plain "PackContent" "Strings" "locale string tables keyed by locale code"
          optional "PackPlayerStart" "SceneId" ReferenceKind.Scene
          reference "PackStartItem" "ItemId" ReferenceKind.Item
          // Export
          plain "ExportSettings" "GameId" "the stable save-folder identity of the exported game"
          optional "ExportSettings" "IconAssetId" ReferenceKind.Asset
          oneOf "ExportSettings" "PixelScale" [ "integer", "Whole-number scaling (crisp)"; "fit", "Fit the window" ]
          plain "ExportSettings" "Targets" "export target names (windows-x64, linux-x64)" ]

    let private byProperty =
        let table = Dictionary<struct (string * string), FieldDeclaration>()
        for declaration in declarations do
            table[struct (declaration.Owner, declaration.Property)] <- declaration
        table

    /// What `owner.property` holds, when it is declared.
    let roleOf (owner: string) (property: string) : FieldRole option =
        match byProperty.TryGetValue(struct (owner, property)) with
        | true, declaration -> Some declaration.Role
        | _ -> None

    let private orEmpty (items: List<'T> | null) : seq<'T> =
        match items with
        | null -> Seq.empty
        | items -> items :> seq<'T>

    let private label (name: string | null) (id: string) =
        match name with
        | null -> id
        | name when String.IsNullOrWhiteSpace name || name = id -> id
        | name when String.Equals(name, id, StringComparison.OrdinalIgnoreCase) -> name
        | name -> sprintf "%s (%s)" name id

    let private entries (idOf: 'T -> string) (nameOf: 'T -> string | null) (items: seq<'T>) : PickerOption list =
        let seen = HashSet<string>()
        [ for item in items do
              let id = idOf item
              if not (obj.ReferenceEquals(id, null)) && seen.Add id then
                  { Id = id; Label = label (nameOf item) id; Missing = false } ]

    let private shorten (text: string | null) =
        match text with
        | null -> ""
        | text when text.Length > 40 -> text.Substring(0, 39) + "…"
        | text -> text

    /// The ids a picker of `kind` offers, in project order. Items, crops, node types, weather and
    /// the calendar come from the content compiler, so the list holds exactly what the game
    /// resolves (the built-in catalog included where the compiler merges it). Pack content is not
    /// offered: packs stay separate layers until they are imported.
    let options (kind: ReferenceKind) (project: GameProject) : PickerOption list =
        match kind with
        | ReferenceKind.Item -> ContentCompiler.items project |> entries (fun i -> i.Id) (fun i -> i.Name)
        | ReferenceKind.Npc -> orEmpty project.Npcs |> entries (fun n -> n.Id) (fun n -> n.Name)
        | ReferenceKind.Scene -> orEmpty project.Scenes |> entries (fun s -> s.Id) (fun s -> s.Name)
        | ReferenceKind.Quest -> orEmpty project.Quests |> entries (fun q -> q.Id) (fun q -> q.Name)
        | ReferenceKind.Dialogue ->
            let names = Dictionary<string, string>()
            for npc in orEmpty project.Npcs do
                names[npc.Id] <- npc.Name
            let owner (d: Dialogue) =
                match names.TryGetValue d.NpcId with
                | true, name -> name
                | _ -> d.NpcId
            Seq.append (orEmpty project.Npcs |> Seq.collect (fun n -> orEmpty n.Dialogue)) (orEmpty project.Dialogues)
            |> entries (fun d -> d.Id) (fun d -> sprintf "%s: %s" (owner d) (shorten d.Text))
        | ReferenceKind.Shop -> orEmpty project.Shops |> entries (fun s -> s.Id) (fun s -> s.Name)
        | ReferenceKind.Recipe -> orEmpty project.Recipes |> entries (fun r -> r.Id) (fun r -> r.Name)
        | ReferenceKind.Crop -> (ContentCompiler.mergeCrops project.CustomCrops).Values |> entries (fun c -> c.Id) (fun c -> c.Name)
        | ReferenceKind.NodeType -> ContentCompiler.nodeTypes project |> entries (fun n -> n.Id) (fun n -> n.Name)
        | ReferenceKind.MachineType -> orEmpty project.MachineTypes |> entries (fun m -> m.Id) (fun m -> m.Name)
        | ReferenceKind.AnimalSpecies -> orEmpty project.AnimalSpecies |> entries (fun s -> s.Id) (fun s -> s.Name)
        | ReferenceKind.Animal -> orEmpty project.Animals |> entries (fun a -> a.Id) (fun a -> a.Name)
        | ReferenceKind.FishTable -> orEmpty project.FishTables |> entries (fun f -> f.Id) (fun f -> f.Name)
        | ReferenceKind.Action -> orEmpty project.Actions |> entries (fun a -> a.Id) (fun a -> a.Name)
        | ReferenceKind.Minigame -> orEmpty project.Minigames |> entries (fun m -> m.Id) (fun m -> m.Name)
        | ReferenceKind.Event -> orEmpty project.Events |> entries (fun e -> e.Id) (fun e -> e.Name)
        | ReferenceKind.Weather -> (ContentCompiler.weather project).Types |> entries (fun w -> w.Id) (fun w -> w.Name)
        | ReferenceKind.Season -> (ContentCompiler.settings project).Calendar.Seasons |> entries (fun s -> s.Id) (fun s -> s.Name)
        | ReferenceKind.Festival -> (ContentCompiler.settings project).Calendar.Festivals |> entries (fun f -> f.Id) (fun f -> f.Name)
        | ReferenceKind.Skill -> SaveSchema.SkillNames |> entries id (fun _ -> (null: string | null))
        | ReferenceKind.Asset -> orEmpty project.CustomAssets |> entries (fun a -> a.Id) (fun a -> a.Name)
        | ReferenceKind.Pack -> orEmpty project.ContentPacks |> entries (fun p -> p.Pack.Manifest.Id) (fun p -> p.Pack.Manifest.Name)
        | ReferenceKind.StationCategory ->
            orEmpty project.MachineTypes |> Seq.collect (fun m -> orEmpty m.StationCategories) |> entries id (fun _ -> (null: string | null))

    /// A picker's entries: the "nothing chosen" entry when `empty` allows it, a "(missing: id)"
    /// entry when `current` names something that is not among `available`, then `available`.
    let pickerEntries (available: PickerOption list) (empty: string option) (current: string | null) : PickerOption list =
        let currentId = match current with null -> "" | s -> s
        let known = currentId.Length = 0 || available |> List.exists (fun o -> o.Id = currentId)
        [ match empty with
          | Some text -> { Id = ""; Label = text; Missing = false }
          | None -> ()
          if not known then { Id = currentId; Label = sprintf "(missing: %s)" currentId; Missing = true }
          yield! available ]

    /// Picker entries for a reference of `kind` holding `current`.
    let picker (kind: ReferenceKind) (empty: string option) (project: GameProject) (current: string | null) =
        pickerEntries (options kind project) empty current

    /// Choice entries as picker options (value, label).
    let choiceOptions (choices: (string * string) list) : PickerOption list =
        choices |> List.map (fun (value, text) -> { Id = value; Label = text; Missing = false })
