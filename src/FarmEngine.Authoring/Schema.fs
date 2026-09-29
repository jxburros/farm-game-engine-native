namespace FarmEngine.Schemas

open FarmEngine.Authoring

// Generated once from the C# records of FarmEngine.Schemas (a port of packages/engine-schemas), then
// kept by hand. JSON: SchemaJson.fs. Numbers are `float` (JS numbers) until the native-numerics
// cutover; `option` is an optional or nullable field; `Extra` keeps the keys the schema does not
// declare (zod `.passthrough()`), in order.

type ActionDef =
    {
        Id: string
        Name: string
        Description: string
        /// All must hold for the action to run (same vocabulary as events).
        Conditions: EventCondition list
        /// Shown when a condition fails; silent when empty.
        FailMessage: string
        /// Applied in order when the action runs (same vocabulary as events).
        Outcomes: EventOutcome list
        /// Energy spent on a successful run (respects the energy toggle). nonnegative.
        EnergyCost: float
        /// Optional single-character play-mode hotkey (max length 1). Reserved gameplay keys (movement/tools/panels) are ignored by hosts.
        Hotkey: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `ActionDef` with every field at its schema default.
    static member Default : ActionDef =
        { Id = ""; Name = ""; Description = ""; Conditions = []; FailMessage = ""; Outcomes = []; EnergyCost = 0.0; Hotkey = None; Extra = [] }

and AnimalSpeciesDefinition =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        /// nonnegative.
        PurchaseCost: float
        /// Item consumed daily; absent = grazes for free.
        FeedItemId: string option
        ProductItemId: string
        /// Days between products (when fed and adult). int, positive.
        ProductIntervalDays: float
        /// int, nonnegative.
        DaysToAdult: float
        /// Renderer hint.
        Color: string
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `AnimalSpeciesDefinition` with every field at its schema default.
    static member Default : AnimalSpeciesDefinition =
        { Id = ""; Name = ""; Visual = None; PurchaseCost = 0.0; FeedItemId = None; ProductItemId = ""; ProductIntervalDays = 1.0; DaysToAdult = 3.0; Color = "#e8d8c3"; Extra = [] }

/// A live animal (persisted per project/save).
and AnimalState =
    {
        Id: string
        SpeciesId: string
        Name: string
        SceneId: string
        /// int.
        X: float
        /// int.
        Y: float
        /// 0..100; fed & petted raise it, neglect lowers it.
        Mood: float
        FedToday: bool
        PettedToday: bool
        /// int.
        AgeDays: float
        /// int.
        DaysSinceProduct: float
        /// Product waiting to be collected.
        ProductReady: bool
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `AnimalState` with every field at its schema default.
    static member Default : AnimalState =
        { Id = ""; SpeciesId = ""; Name = ""; SceneId = ""; X = 0.0; Y = 0.0; Mood = 70.0; FedToday = false; PettedToday = false; AgeDays = 0.0; DaysSinceProduct = 0.0; ProductReady = false; Extra = [] }

and AnimationClip =
    {
        /// min length 1.
        Name: string
        Loop: bool
        /// 1..1024 frames.
        Frames: ArtFrame list
    }

    /// A `AnimationClip` with every field at its schema default.
    static member Default : AnimationClip =
        { Name = ""; Loop = true; Frames = [] }

/// Rectangles are in source pixels; display size remains independent of art resolution.
and ArtFrame =
    {
        AssetId: string option
        /// int, nonnegative.
        X: float
        /// int, nonnegative.
        Y: float
        /// int, positive.
        Width: float
        /// int, positive.
        Height: float
        /// int, positive.
        Ticks: float
    }

    /// A `ArtFrame` with every field at its schema default.
    static member Default : ArtFrame =
        { AssetId = None; X = 0.0; Y = 0.0; Width = 0.0; Height = 0.0; Ticks = 6.0 }

and CalendarConfig =
    {
        /// Ordered list of seasons; the calendar year is the sum of their lengths.
        Seasons: CalendarSeason list
        /// Named festival days, each pinned to a season + day-of-season.
        Festivals: CalendarFestival list
    }

    /// A `CalendarConfig` with every field at its schema default.
    static member Default : CalendarConfig =
        { Seasons = [ ({ Id = "spring"; Name = "Spring"; Days = 28.0 } : CalendarSeason); ({ Id = "summer"; Name = "Summer"; Days = 28.0 } : CalendarSeason); ({ Id = "fall"; Name = "Fall"; Days = 28.0 } : CalendarSeason); ({ Id = "winter"; Name = "Winter"; Days = 28.0 } : CalendarSeason) ]; Festivals = [] }

and CalendarFestival =
    {
        Id: string
        Name: string
        /// Season this festival falls in (matches a CalendarSeason id).
        SeasonId: string
        /// 1-based day within that season. int, positive.
        Day: float
    }

    /// A `CalendarFestival` with every field at its schema default.
    static member Default : CalendarFestival =
        { Id = ""; Name = ""; SeasonId = ""; Day = 0.0 }

and CalendarSeason =
    {
        Id: string
        Name: string
        /// In-game days this season lasts. int, positive.
        Days: float
    }

    /// A `CalendarSeason` with every field at its schema default.
    static member Default : CalendarSeason =
        { Id = ""; Name = ""; Days = 0.0 }

and ContentPack =
    {
        Manifest: PackManifest
        Content: PackContent
        Plugins: PackPlugin list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `ContentPack` with every field at its schema default.
    static member Default : ContentPack =
        { Manifest = PackManifest.Default; Content = PackContent.Default; Plugins = []; Extra = [] }

/// A dropped/growing crop instance on a tile (game state, not content).
and Crop =
    {
        Type: string
        /// Legacy wall-clock plant timestamp (ms). Superseded by plantedOnDay from schema v4.
        PlantedAt: float
        /// Absolute in-game day the crop was planted (schema v4+). int.
        PlantedOnDay: float option
        /// Watered in-game days accumulated toward growthDays (schema v4+).
        DaysGrown: float option
        /// Killed by season change; renders dead and can be cleared.
        Withered: bool option
        Stage: float
        Watered: bool
        LastWateredDay: float option
        /// One of `CropQualities`.
        Quality: string
        /// One of `CropMutations` or `null` (required key).
        Mutation: string option
        IsMultiTileRoot: bool option
        MultiTileId: string option
        HarvestCount: float
        DaysWithoutWater: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Crop` with every field at its schema default.
    static member Default : Crop =
        { Type = ""; PlantedAt = 0.0; PlantedOnDay = None; DaysGrown = None; Withered = None; Stage = 0.0; Watered = false; LastWateredDay = None; Quality = ""; Mutation = None; IsMultiTileRoot = None; MultiTileId = None; HarvestCount = 0.0; DaysWithoutWater = 0.0; Extra = [] }

and CropDefinition =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        SeedCost: float
        BaseHarvestValue: float
        /// Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.
        GrowthTime: float
        /// In-game days from planting to maturity (authoritative from schema v4). positive.
        GrowthDays: float option
        /// int, positive.
        Stages: float
        Seasons: string list
        RegrowthTime: float option
        /// In-game days between repeat harvests for regrowing crops. positive.
        RegrowthDays: float option
        CanRegrow: bool
        MultiTile: CropMultiTile option
        MutationChance: float option
        YieldMin: float
        YieldMax: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `CropDefinition` with every field at its schema default.
    static member Default : CropDefinition =
        { Id = ""; Name = ""; Visual = None; SeedCost = 0.0; BaseHarvestValue = 0.0; GrowthTime = 0.0; GrowthDays = None; Stages = 0.0; Seasons = []; RegrowthTime = None; RegrowthDays = None; CanRegrow = false; MultiTile = None; MutationChance = None; YieldMin = 0.0; YieldMax = 0.0; Extra = [] }

/// TS `CropDefinitionSchema.multiTile` (inline object).
and CropMultiTile =
    {
        /// int, positive.
        Width: float
        /// int, positive.
        Height: float
    }

    /// A `CropMultiTile` with every field at its schema default.
    static member Default : CropMultiTile =
        { Width = 0.0; Height = 0.0 }

and CustomAsset =
    {
        Id: string
        Name: string
        /// One of `CustomAssetTypes`.
        Type: string
        /// int, positive.
        Width: float option
        /// int, positive.
        Height: float option
        Animations: (AnimationClip list) option
        DataUrl: string
        TileType: string option
        /// Present when the image is an animated sprite sheet (M7).
        Sheet: SpriteSheet option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `CustomAsset` with every field at its schema default.
    static member Default : CustomAsset =
        { Id = ""; Name = ""; Type = ""; Width = None; Height = None; Animations = None; DataUrl = ""; TileType = None; Sheet = None; Extra = [] }

/// TS `CustomCropDefinitionSchema = CropDefinitionSchema.extend({ customAsset })` (zod 3 `extend` keeps passthrough). C# records can't extend a sealed record, so the fields are repeated.
and CustomCropDefinition =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        SeedCost: float
        BaseHarvestValue: float
        /// Legacy wall-clock growth duration (ms). Kept for pre-v4 data; the engine uses growthDays.
        GrowthTime: float
        /// In-game days from planting to maturity (authoritative from schema v4). positive.
        GrowthDays: float option
        /// int, positive.
        Stages: float
        Seasons: string list
        RegrowthTime: float option
        /// In-game days between repeat harvests for regrowing crops. positive.
        RegrowthDays: float option
        CanRegrow: bool
        MultiTile: CropMultiTile option
        MutationChance: float option
        YieldMin: float
        YieldMax: float
        CustomAsset: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `CustomCropDefinition` with every field at its schema default.
    static member Default : CustomCropDefinition =
        { Id = ""; Name = ""; Visual = None; SeedCost = 0.0; BaseHarvestValue = 0.0; GrowthTime = 0.0; GrowthDays = None; Stages = 0.0; Seasons = []; RegrowthTime = None; RegrowthDays = None; CanRegrow = false; MultiTile = None; MutationChance = None; YieldMin = 0.0; YieldMax = 0.0; CustomAsset = None; Extra = [] }

and Dialogue =
    {
        Id: string
        NpcId: string
        Text: string
        Options: DialogueOption list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Dialogue` with every field at its schema default.
    static member Default : Dialogue =
        { Id = ""; NpcId = ""; Text = ""; Options = []; Extra = [] }

and DialogueOption =
    {
        Text: string
        NextDialogueId: string option
        GiveItem: string option
        GiveItemQuantity: float option
        TakeMoney: float option
        GiveMoney: float option
        EventFlag: string option
        RequiresItem: string option
        RequiresFlag: string option
        /// Choosing this option closes the dialogue and opens the given shop (M2).
        OpenShopId: string option
        /// Choosing this option offers/starts the given quest (M3 quest-giver binding).
        OfferQuestId: string option
        /// Option only shown at/above this friendship (M4 heart-gated dialogue).
        RequiresFriendship: float option
        /// Choosing this option performs a creator-defined action.
        ActionId: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `DialogueOption` with every field at its schema default.
    static member Default : DialogueOption =
        { Text = ""; NextDialogueId = None; GiveItem = None; GiveItemQuantity = None; TakeMoney = None; GiveMoney = None; EventFlag = None; RequiresItem = None; RequiresFlag = None; OpenShopId = None; OfferQuestId = None; RequiresFriendship = None; ActionId = None; Extra = [] }

/// A single event/action outcome. Not a discriminated union: one flat object whose `type` selects which optional fields apply.
and EventOutcome =
    {
        /// One of `EventOutcomeTypes`.
        Type: string
        Message: string option
        ItemId: string option
        ItemQuantity: float option
        /// finite.
        Amount: float option
        /// int, 0..10.
        Radius: float option
        FlagName: string option
        QuestId: string option
        NpcId: string option
        DialogueId: string option
        TileX: float option
        TileY: float option
        /// One of `TileTypes`.
        NewTileType: string option
        SceneId: string option
        X: float option
        Y: float option
        SoundId: string option
        ActionId: string option
        MinigameId: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `EventOutcome` with every field at its schema default.
    static member Default : EventOutcome =
        { Type = ""; Message = None; ItemId = None; ItemQuantity = None; Amount = None; Radius = None; FlagName = None; QuestId = None; NpcId = None; DialogueId = None; TileX = None; TileY = None; NewTileType = None; SceneId = None; X = None; Y = None; SoundId = None; ActionId = None; MinigameId = None; Extra = [] }

/// Additive project export settings. The web project schema is passthrough, so the web editor can keep this block even before it exposes native export controls.
and ExportSettings =
    {
        Title: string option
        ExecutableName: string option
        Version: string option
        /// Stable save-folder identity; never derive it from a later project rename.
        GameId: string
        Author: string option
        Company: string option
        IconAssetId: string option
        Window: ExportWindow
        /// `integer` or `fit`.
        PixelScale: string
        Credits: string option
        Targets: string list
    }

    /// A `ExportSettings` with every field at its schema default.
    static member Default : ExportSettings =
        { Title = None; ExecutableName = None; Version = None; GameId = ""; Author = None; Company = None; IconAssetId = None; Window = ExportWindow.Default; PixelScale = "integer"; Credits = None; Targets = [ "windows-x64"; "linux-x64" ] }

/// Default window for a standalone exported game.
and ExportWindow =
    {
        Width: int
        Height: int
        Fullscreen: bool
    }

    /// A `ExportWindow` with every field at its schema default.
    static member Default : ExportWindow =
        { Width = 1280; Height = 800; Fullscreen = false }

and ExportedGame =
    {
        /// int.
        SchemaVersion: float option
        Version: string
        Name: string
        Scenes: Scene list
        Npcs: Npc list
        Items: Item list
        Events: GameEvent list
        Dialogues: Dialogue list
        Quests: Quest list
        StartSceneId: string
        CustomAssets: CustomAsset list
        CustomCrops: (CustomCropDefinition list) option
        PlayerCustomImage: string option
        PlayerVisual: VisualRef option
        Graphics: GraphicsSettings option
        GamePanels: (GamePanel list) option
        CurrentSeason: string
        CurrentDay: float
        CurrentTimeMinutes: float
        /// int.
        CurrentYear: float
        GameStartTime: float
        Shops: ShopDefinition list
        NodeTypes: NodeTypeDefinition list
        Settings: ProjectSettings
        Recipes: RecipeDefinition list
        MachineTypes: MachineTypeDefinition list
        Weather: WeatherConfig
        AnimalSpecies: AnimalSpeciesDefinition list
        Animals: AnimalState list
        FishTables: FishTable list
        Mine: MineConfig
        Actions: ActionDef list
        Minigames: MinigameDef list
        ContentPacks: PackInstallation list
        /// Player start state (M6): included so exported games start identically.
        Player: Player option
        CurrentWeatherId: string option
        SocialState: ((string * NpcSocialState) list) option
        /// int, nonnegative.
        MineDeepestFloor: float option
        QuarantinedItems: (InventorySlot list) option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `ExportedGame` with every field at its schema default.
    static member Default : ExportedGame =
        { SchemaVersion = None; Version = ""; Name = ""; Scenes = []; Npcs = []; Items = []; Events = []; Dialogues = []; Quests = []; StartSceneId = ""; CustomAssets = []; CustomCrops = None; PlayerCustomImage = None; PlayerVisual = None; Graphics = None; GamePanels = None; CurrentSeason = ""; CurrentDay = 0.0; CurrentTimeMinutes = 0.0; CurrentYear = 0.0; GameStartTime = 0.0; Shops = []; NodeTypes = []; Settings = ProjectSettings.Default; Recipes = []; MachineTypes = []; Weather = WeatherConfig.Default; AnimalSpecies = []; Animals = []; FishTables = []; Mine = MineConfig.Default; Actions = []; Minigames = []; ContentPacks = []; Player = None; CurrentWeatherId = None; SocialState = None; MineDeepestFloor = None; QuarantinedItems = None; Extra = [] }

and FishTable =
    {
        Id: string
        Name: string
        /// Restrict to seasons (absent = all).
        Seasons: (string list) option
        /// Restrict to scenes (absent = any water).
        SceneIds: (string list) option
        Entries: FishTableEntry list
        /// Chance (0..1) a cast catches junk instead of rolling the table.
        JunkChance: float
        JunkItemId: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `FishTable` with every field at its schema default.
    static member Default : FishTable =
        { Id = ""; Name = ""; Seasons = None; SceneIds = None; Entries = []; JunkChance = 0.15; JunkItemId = None; Extra = [] }

and FishTableEntry =
    {
        ItemId: string
        /// positive.
        Weight: float
        /// 0..1 — harder fish escape low-tier rods more often.
        Difficulty: float
    }

    /// A `FishTableEntry` with every field at its schema default.
    static member Default : FishTableEntry =
        { ItemId = ""; Weight = 0.0; Difficulty = 0.3 }

and GameContent =
    {
        /// int.
        ContentVersion: float
        /// Crop definitions by id (built-in merged with project custom crops).
        Crops: (string * CropDefinition) list
        Items: Item list
        Npcs: Npc list
        Dialogues: Dialogue list
        Quests: Quest list
        Events: GameEvent list
        Shops: ShopDefinition list
        NodeTypes: NodeTypeDefinition list
        Settings: ProjectSettings
        Recipes: RecipeDefinition list
        MachineTypes: MachineTypeDefinition list
        Weather: WeatherConfig
        AnimalSpecies: AnimalSpeciesDefinition list
        FishTables: FishTable list
        Mine: MineConfig
        /// Creator-defined actions (extensibility layer).
        Actions: ActionDef list
        /// Declared minigames (extensibility layer).
        Minigames: MinigameDef list
        /// Authored initial scenes — the template the world state is created from.
        Scenes: Scene list
        StartSceneId: string
    }

    /// A `GameContent` with every field at its schema default.
    static member Default : GameContent =
        { ContentVersion = 0.0; Crops = []; Items = []; Npcs = []; Dialogues = []; Quests = []; Events = []; Shops = []; NodeTypes = []; Settings = ProjectSettings.Default; Recipes = []; MachineTypes = []; Weather = WeatherConfig.Default; AnimalSpecies = []; FishTables = []; Mine = MineConfig.Default; Actions = []; Minigames = []; Scenes = []; StartSceneId = "" }

and GameEvent =
    {
        Id: string
        Name: string
        /// Scene the event lives in; empty string = evaluated in every scene.
        SceneId: string
        /// One of `EventTriggers`.
        Trigger: string
        Conditions: EventCondition list
        Outcomes: EventOutcome list
        Active: bool
        Repeatable: bool
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `GameEvent` with every field at its schema default.
    static member Default : GameEvent =
        { Id = ""; Name = ""; SceneId = ""; Trigger = ""; Conditions = []; Outcomes = []; Active = false; Repeatable = false; Extra = [] }

and GamePanel =
    {
        Id: string
        Title: string
        VisibleFlag: string option
        /// At most 40 entries.
        Entries: GamePanelEntry list
    }

    /// A `GamePanel` with every field at its schema default.
    static member Default : GamePanel =
        { Id = ""; Title = ""; VisibleFlag = None; Entries = [] }

/// TS `GamePanelSchema.entries` item (inline object).
and GamePanelEntry =
    {
        Label: string
        /// One of `GamePanelEntryKinds`.
        Kind: string
        Value: string
    }

    /// A `GamePanelEntry` with every field at its schema default.
    static member Default : GamePanelEntry =
        { Label = ""; Kind = ""; Value = "" }

and GameProject =
    {
        /// int.
        SchemaVersion: float
        Id: string
        Name: string
        Version: string
        Scenes: Scene list
        Npcs: Npc list
        Items: Item list
        Events: GameEvent list
        Dialogues: Dialogue list
        Quests: Quest list
        Player: Player
        EventFlags: (string * bool) list
        StartSceneId: string
        /// One of `EditorModes`.
        Mode: string
        /// One of `TileTypes`.
        SelectedTileType: string
        SelectedTileVisual: VisualRef option
        SelectedNpcId: string option
        SelectedItemId: string option
        CurrentTime: float
        CustomAssets: CustomAsset list
        CustomCrops: (CustomCropDefinition list) option
        PlayerCustomImage: string option
        PlayerVisual: VisualRef option
        Graphics: GraphicsSettings option
        GamePanels: (GamePanel list) option
        CurrentSeason: string
        CurrentDay: float
        /// Minute-of-day of the game clock (v4+).
        CurrentTimeMinutes: float
        /// int.
        CurrentYear: float
        GameStartTime: float
        Shops: ShopDefinition list
        NodeTypes: NodeTypeDefinition list
        Settings: ProjectSettings
        Recipes: RecipeDefinition list
        MachineTypes: MachineTypeDefinition list
        Weather: WeatherConfig
        AnimalSpecies: AnimalSpeciesDefinition list
        Animals: AnimalState list
        FishTables: FishTable list
        Mine: MineConfig
        Actions: ActionDef list
        Minigames: MinigameDef list
        /// Installed content packs (M5). Array order = load order.
        ContentPacks: PackInstallation list
        /// Optional native export identity, window and target settings (additive to schema v8).
        Export: ExportSettings option
        /// Serialized PRNG state so play sessions resume deterministically (additive, optional).
        RngState: RngState option
        /// Today's weather id (mirrors GameState.clock.weatherId).
        CurrentWeatherId: string option
        /// Friendship & gifting state per NPC (mirrors GameState.social).
        SocialState: ((string * NpcSocialState) list) option
        /// Deepest mine floor reached (mirrors GameState.mine.deepestFloor). int, nonnegative.
        MineDeepestFloor: float option
        /// Items whose owning pack is missing/disabled (mirrors GameState.quarantinedItems).
        QuarantinedItems: (InventorySlot list) option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `GameProject` with every field at its schema default.
    static member Default : GameProject =
        { SchemaVersion = 0.0; Id = ""; Name = ""; Version = ""; Scenes = []; Npcs = []; Items = []; Events = []; Dialogues = []; Quests = []; Player = Player.Default; EventFlags = []; StartSceneId = ""; Mode = ""; SelectedTileType = ""; SelectedTileVisual = None; SelectedNpcId = None; SelectedItemId = None; CurrentTime = 0.0; CustomAssets = []; CustomCrops = None; PlayerCustomImage = None; PlayerVisual = None; Graphics = None; GamePanels = None; CurrentSeason = ""; CurrentDay = 0.0; CurrentTimeMinutes = 0.0; CurrentYear = 0.0; GameStartTime = 0.0; Shops = []; NodeTypes = []; Settings = ProjectSettings.Default; Recipes = []; MachineTypes = []; Weather = WeatherConfig.Default; AnimalSpecies = []; Animals = []; FishTables = []; Mine = MineConfig.Default; Actions = []; Minigames = []; ContentPacks = []; Export = None; RngState = None; CurrentWeatherId = None; SocialState = None; MineDeepestFloor = None; QuarantinedItems = None; Extra = [] }

and GiftTastes =
    {
        Loved: string list
        Liked: string list
        Disliked: string list
        Hated: string list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `GiftTastes` with every field at its schema default.
    static member Default : GiftTastes =
        { Loved = []; Liked = []; Disliked = []; Hated = []; Extra = [] }

and GraphicsSettings =
    {
        PixelArt: bool
    }

    /// A `GraphicsSettings` with every field at its schema default.
    static member Default : GraphicsSettings =
        { PixelArt = true }

/// An integer tile coordinate: TS inline `{ x: int, y: int }` objects in `NPCSchema.patrolPoints` and `NpcStateSchema.path`.
and GridPoint =
    {
        /// int.
        X: float
        /// int.
        Y: float
    }

    /// A `GridPoint` with every field at its schema default.
    static member Default : GridPoint =
        { X = 0.0; Y = 0.0 }

and InventorySlot =
    {
        Item: Item
        Quantity: float
    }

    /// A `InventorySlot` with every field at its schema default.
    static member Default : InventorySlot =
        { Item = Item.Default; Quantity = 0.0 }

and Item =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        Description: string
        /// One of `ItemTypes`.
        Type: string
        Stackable: bool
        MaxStack: float
        Value: float
        CropType: string option
        CustomImage: string option
        /// One of `ToolTypes`.
        ToolType: string option
        ToolPower: float option
        /// Tool tier: 1 = basic. Higher tiers hit harder, cost less energy, gain AoE. int, positive.
        ToolTier: float option
        Durability: float option
        MaxDurability: float option
        /// Action performed when the item is used from the inventory.
        UseActionId: string option
        /// Consume one of the item on a successful use.
        ConsumeOnUse: bool option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Item` with every field at its schema default.
    static member Default : Item =
        { Id = ""; Name = ""; Visual = None; Description = ""; Type = ""; Stackable = false; MaxStack = 0.0; Value = 0.0; CropType = None; CustomImage = None; ToolType = None; ToolPower = None; ToolTier = None; Durability = None; MaxDurability = None; UseActionId = None; ConsumeOnUse = None; Extra = [] }

/// TS `TileMachineSchema.processing` (inline object): an in-flight machine job.
and MachineProcessing =
    {
        RecipeId: string
        CompletesAtMinute: float
    }

    /// A `MachineProcessing` with every field at its schema default.
    static member Default : MachineProcessing =
        { RecipeId = ""; CompletesAtMinute = 0.0 }

and MachineTypeDefinition =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        Description: string
        /// Renderer hint.
        Color: string
        /// Item consumed to place this machine (crafted or bought).
        ItemId: string option
        BlocksMovement: bool
        /// Station categories a placed machine of this type provides to nearby hand-crafting (e.g. a Kitchen provides 'cooking').
        StationCategories: string list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `MachineTypeDefinition` with every field at its schema default.
    static member Default : MachineTypeDefinition =
        { Id = ""; Name = ""; Visual = None; Description = ""; Color = "#9a7b4f"; ItemId = None; BlocksMovement = true; StationCategories = []; Extra = [] }

and MineBand =
    {
        /// int, positive.
        FromFloor: float
        /// int, positive.
        ToFloor: float
        /// Weighted node types spawned in this depth band.
        Rocks: MineRockWeight list
        /// Rock density (fraction of floor tiles occupied). 0..1.
        Density: float
    }

    /// A `MineBand` with every field at its schema default.
    static member Default : MineBand =
        { FromFloor = 0.0; ToFloor = 0.0; Rocks = []; Density = 0.35 }

and MineConfig =
    {
        Enabled: bool
        /// Scene holding the mine entrance (descend via the entrance tile).
        EntranceSceneId: string option
        /// int.
        EntranceX: float option
        /// int.
        EntranceY: float option
        /// int, positive.
        Floors: float
        /// int, positive.
        FloorWidth: float
        /// int, positive.
        FloorHeight: float
        Bands: MineBand list
        /// Chance a broken rock reveals the ladder down. 0..1.
        LadderChance: float
        /// Elevator checkpoint every N floors. int, positive.
        ElevatorEvery: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `MineConfig` with every field at its schema default.
    static member Default : MineConfig =
        { Enabled = false; EntranceSceneId = None; EntranceX = None; EntranceY = None; Floors = 20.0; FloorWidth = 14.0; FloorHeight = 12.0; Bands = []; LadderChance = 0.18; ElevatorEvery = 5.0; Extra = [] }

/// TS `MineBandSchema.rocks` item (inline object): a weighted node type.
and MineRockWeight =
    {
        NodeTypeId: string
        /// positive.
        Weight: float
    }

    /// A `MineRockWeight` with every field at its schema default.
    static member Default : MineRockWeight =
        { NodeTypeId = ""; Weight = 0.0 }

and MinigameDef =
    {
        Id: string
        Name: string
        /// Implementation key looked up in the host's minigame registry ('timing-bar' ships with the engine; game code registers custom kinds). Unknown kinds fall back to a neutral confirm that scores 0.5.
        Kind: string
        /// Kind-specific tuning, passed verbatim to the implementation (`z.unknown()` values).
        Config: (string * Json) list
        /// Declarative consequences by score tier (highest matching minScore).
        ResultTiers: MinigameResultTier list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `MinigameDef` with every field at its schema default.
    static member Default : MinigameDef =
        { Id = ""; Name = ""; Kind = "timing-bar"; Config = []; ResultTiers = []; Extra = [] }

and MinigameResultTier =
    {
        /// Tier applies when score ≥ minScore; the highest matching tier wins. 0..1.
        MinScore: float
        Outcomes: EventOutcome list
    }

    /// A `MinigameResultTier` with every field at its schema default.
    static member Default : MinigameResultTier =
        { MinScore = 0.0; Outcomes = [] }

and MovementConfig =
    {
        /// Player walk speed in tiles per second (free movement). positive.
        PlayerSpeed: float
    }

    /// A `MovementConfig` with every field at its schema default.
    static member Default : MovementConfig =
        { PlayerSpeed = 4.5 }

/// Weighted drop-table entry for a gathering node.
and NodeDrop =
    {
        ItemId: string
        /// int, nonnegative.
        Min: float
        /// int, nonnegative.
        Max: float
        /// positive.
        Weight: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `NodeDrop` with every field at its schema default.
    static member Default : NodeDrop =
        { ItemId = ""; Min = 0.0; Max = 0.0; Weight = 0.0; Extra = [] }

/// A gathering node type (tree, rock, weeds, …) — content-defined so mods and projects can add their own.
and NodeTypeDefinition =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        /// Number of tool hits required to break the node. int, positive.
        Health: float
        /// One of `ToolTypes`.
        RequiredTool: string
        /// Minimum tool tier required (tools default to tier 1). int, positive.
        RequiredToolTier: float
        /// Weighted drop table; each hit that depletes the node rolls once per entry range.
        Drops: NodeDrop list
        RespawnDays: float option option
        /// Renderer hint (hex color).
        Color: string
        /// Whether the node blocks movement while present.
        BlocksMovement: bool
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `NodeTypeDefinition` with every field at its schema default.
    static member Default : NodeTypeDefinition =
        { Id = ""; Name = ""; Visual = None; Health = 0.0; RequiredTool = ""; RequiredToolTier = 1.0; Drops = []; RespawnDays = None; Color = "#7a5a3a"; BlocksMovement = true; Extra = [] }

/// TS `NPC` (`NPCSchema`).
and Npc =
    {
        Id: string
        Name: string
        Visual: VisualRef option
        X: float
        Y: float
        SceneId: string
        Dialogue: Dialogue list
        CanMove: bool
        /// One of `NpcMovePatterns`.
        MovePattern: string option
        /// Max tiles from home for the wander pattern (default 3). int, positive.
        WanderRadius: float option
        /// Waypoints for the patrol pattern (visited in order, looping).
        PatrolPoints: (GridPoint list) option
        /// Time-based schedule (M3): sorted by minute; latest passed entry wins.
        Schedule: (NpcScheduleEntry list) option
        /// Gift preferences (M4 social); unlisted items are neutral.
        GiftTastes: GiftTastes option
        /// Birthday: day-of-season (1-28) within birthSeason, doubles gift effects.
        Birthday: NpcBirthday option
        Appearance: string
        CustomImage: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Npc` with every field at its schema default.
    static member Default : Npc =
        { Id = ""; Name = ""; Visual = None; X = 0.0; Y = 0.0; SceneId = ""; Dialogue = []; CanMove = false; MovePattern = None; WanderRadius = None; PatrolPoints = None; Schedule = None; GiftTastes = None; Birthday = None; Appearance = ""; CustomImage = None; Extra = [] }

/// TS `NPCSchema.birthday` (inline object).
and NpcBirthday =
    {
        /// One of the classic seasons ('spring' | 'summer' | 'fall' | 'winter').
        Season: string
        /// int.
        Day: float
    }

    /// A `NpcBirthday` with every field at its schema default.
    static member Default : NpcBirthday =
        { Season = ""; Day = 0.0 }

/// A scheduled destination: at `minute` (of day), head to (sceneId, x, y).
and NpcScheduleEntry =
    {
        Minute: float
        SceneId: string
        /// int.
        X: float
        /// int.
        Y: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `NpcScheduleEntry` with every field at its schema default.
    static member Default : NpcScheduleEntry =
        { Minute = 0.0; SceneId = ""; X = 0.0; Y = 0.0; Extra = [] }

/// Per-NPC social state (friendship points etc.).
and NpcSocialState =
    {
        Friendship: float
        /// int.
        GiftsToday: float
        /// int.
        LastGiftDay: float option
    }

    /// A `NpcSocialState` with every field at its schema default.
    static member Default : NpcSocialState =
        { Friendship = 0.0; GiftsToday = 0.0; LastGiftDay = None }

and PackContent =
    {
        Crops: CropDefinition list
        Items: Item list
        Recipes: RecipeDefinition list
        MachineTypes: MachineTypeDefinition list
        NodeTypes: NodeTypeDefinition list
        AnimalSpecies: AnimalSpeciesDefinition list
        FishTables: FishTable list
        WeatherTypes: WeatherTypeDefinition list
        Npcs: Npc list
        Dialogues: Dialogue list
        Scenes: Scene list
        Events: GameEvent list
        Quests: Quest list
        Shops: ShopDefinition list
        Actions: ActionDef list
        Minigames: MinigameDef list
        PlayerStart: PackPlayerStart option
        /// Per-locale string tables for game text (M7 i18n). Keys address content fields: `item:{id}:name`, `item:{id}:description`, `dialogue:{id}:text`, `quest:{id}:name`, `quest:{id}:description`. The authored text is the fallback locale.
        Strings: (string * (string * string) list) list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `PackContent` with every field at its schema default.
    static member Default : PackContent =
        { Crops = []; Items = []; Recipes = []; MachineTypes = []; NodeTypes = []; AnimalSpecies = []; FishTables = []; WeatherTypes = []; Npcs = []; Dialogues = []; Scenes = []; Events = []; Quests = []; Shops = []; Actions = []; Minigames = []; PlayerStart = None; Strings = []; Extra = [] }

and PackDependency =
    {
        PackId: string
        Version: string option
    }

    /// A `PackDependency` with every field at its schema default.
    static member Default : PackDependency =
        { PackId = ""; Version = None }

/// How a project stores an installed pack. Array order = load order.
and PackInstallation =
    {
        Pack: ContentPack
        Enabled: bool
    }

    /// A `PackInstallation` with every field at its schema default.
    static member Default : PackInstallation =
        { Pack = ContentPack.Default; Enabled = true }

and PackManifest =
    {
        /// Must match `^[a-z0-9][a-z0-9-]*$` (see `PacksSchema.PackIdPattern`).
        Id: string
        Name: string
        Version: string
        Description: string option
        Author: string option
        /// Semver range: '*', exact '1.2.3', '^1.2.3' or '>=1.2.3'.
        EngineCompatibility: string
        /// Base packs (content-default) keep their plain IDs; all other packs get their definitions namespaced to `packId:localId` at load time.
        Base: bool
        Dependencies: PackDependency list
        /// Fully-qualified IDs this pack intentionally replaces. Redefining an existing ID without declaring it here is a conflict surfaced in the Problems panel (the earlier definition wins).
        Overrides: string list
        Permissions: PackPermissions
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `PackManifest` with every field at its schema default.
    static member Default : PackManifest =
        { Id = ""; Name = ""; Version = ""; Description = None; Author = None; EngineCompatibility = "*"; Base = false; Dependencies = []; Overrides = []; Permissions = PackPermissions.Default; Extra = [] }

and PackPermissions =
    {
        /// Hook names the pack's plugins may subscribe to (user-approved at install).
        Hooks: string list
        /// May contribute content definitions (the normal case).
        ContentInject: bool
        /// Reserved: declarative UI panels (not yet implemented).
        UiPanels: bool
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `PackPermissions` with every field at its schema default.
    static member Default : PackPermissions =
        { Hooks = []; ContentInject = true; UiPanels = false; Extra = [] }

/// Optional player-start block so a base pack can express the whole starter game.
and PackPlayerStart =
    {
        SceneId: string option
        /// int.
        X: float option
        /// int.
        Y: float option
        Money: float option
        Inventory: PackStartItem list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `PackPlayerStart` with every field at its schema default.
    static member Default : PackPlayerStart =
        { SceneId = None; X = None; Y = None; Money = None; Inventory = []; Extra = [] }

/// A sandboxed plugin: `source` is the body of `function (api) { ... }` and registers handlers with `api.on(hookName, fn)`. Handlers return an array of mutations (validated as `PluginMutation`) — never raw state access.
and PackPlugin =
    {
        Id: string
        Name: string option
        /// Hooks this plugin wants; effective set = intersection with manifest permissions.hooks.
        Hooks: string list
        Source: string
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `PackPlugin` with every field at its schema default.
    static member Default : PackPlugin =
        { Id = ""; Name = None; Hooks = []; Source = ""; Extra = [] }

/// TS `PackPlayerStartSchema.inventory` item (inline object).
and PackStartItem =
    {
        ItemId: string
        /// int, min 1.
        Quantity: float
    }

    /// A `PackStartItem` with every field at its schema default.
    static member Default : PackStartItem =
        { ItemId = ""; Quantity = 0.0 }

/// The project's player start/record (TS `PlayerSchema`); runtime uses `PlayerState`.
and Player =
    {
        X: float
        Y: float
        /// One of `Directions`.
        Direction: string
        SceneId: string
        Inventory: InventorySlot list
        MaxInventorySize: float
        Money: float
        Energy: float option
        MaxEnergy: float option
        /// Per-category skill XP/levels (M4g); mirrored from GameState on save.
        Skills: ((string * SkillState) list) option
        ActiveQuests: string list
        CompletedQuests: string list
        EquippedTool: string option
        /// Pixel X position for smooth movement interpolation
        PixelX: float
        /// Pixel Y position for smooth movement interpolation
        PixelY: float
        /// Target pixel X for interpolation
        TargetX: float
        /// Target pixel Y for interpolation
        TargetY: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Player` with every field at its schema default.
    static member Default : Player =
        { X = 0.0; Y = 0.0; Direction = ""; SceneId = ""; Inventory = []; MaxInventorySize = 0.0; Money = 0.0; Energy = None; MaxEnergy = None; Skills = None; ActiveQuests = []; CompletedQuests = []; EquippedTool = None; PixelX = 0.0; PixelY = 0.0; TargetX = 0.0; TargetY = 0.0; Extra = [] }

and ProjectSettings =
    {
        Movement: MovementConfig
        EnergyEnabled: bool
        /// positive.
        MaxEnergy: float
        /// Fraction of energy restored after a collapse (vs full sleep). 0..1.
        CollapseEnergyFraction: float
        /// Money penalty charged on collapse. nonnegative.
        CollapseMoneyPenalty: float
        Time: TimeConfig
        /// Creator-configurable calendar (M9): seasons, their lengths, and festival days.
        Calendar: CalendarConfig
        /// Player skills (M4g): XP per action category, levels unlock recipes.
        SkillsEnabled: bool
        /// XP thresholds per level (index = level).
        SkillLevelCurve: float list
        /// Game-text locale (M7 i18n): packs may carry per-locale string tables.
        Locale: string
        /// Optional, creator-controlled credit shown in exported games.
        ShowMadeWithCredit: bool
    }

    /// A `ProjectSettings` with every field at its schema default.
    static member Default : ProjectSettings =
        { Movement = MovementConfig.Default; EnergyEnabled = true; MaxEnergy = 100.0; CollapseEnergyFraction = 0.5; CollapseMoneyPenalty = 50.0; Time = TimeConfig.Default; Calendar = CalendarConfig.Default; SkillsEnabled = true; SkillLevelCurve = [ 0.0; 50.0; 150.0; 300.0; 500.0; 750.0; 1050.0; 1400.0; 1800.0; 2250.0 ]; Locale = "en"; ShowMadeWithCredit = false }

and Quest =
    {
        Id: string
        Name: string
        Description: string
        Giver: string option
        /// One of `QuestStatuses`.
        Status: string
        Objectives: QuestObjective list
        Rewards: QuestRewards
        Prerequisites: (string list) option
        AutoStart: bool option
        Repeatable: bool option
        /// Seasonal availability (M3): quest only offered/auto-started in these seasons.
        AvailableSeasons: (string list) option
        /// Absolute-day window (M3): offered from/until these days (inclusive).
        AvailableFromDay: float option
        AvailableToDay: float option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Quest` with every field at its schema default.
    static member Default : Quest =
        { Id = ""; Name = ""; Description = ""; Giver = None; Status = ""; Objectives = []; Rewards = QuestRewards.Default; Prerequisites = None; AutoStart = None; Repeatable = None; AvailableSeasons = None; AvailableFromDay = None; AvailableToDay = None; Extra = [] }

and QuestObjective =
    {
        Id: string
        /// One of `QuestObjectiveTypes`.
        Type: string
        Description: string
        TargetItemId: string option
        TargetItemQuantity: float option
        TargetCropType: string option
        TargetCropQuantity: float option
        TargetNpcId: string option
        TargetSceneId: string option
        Completed: bool
        Progress: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `QuestObjective` with every field at its schema default.
    static member Default : QuestObjective =
        { Id = ""; Type = ""; Description = ""; TargetItemId = None; TargetItemQuantity = None; TargetCropType = None; TargetCropQuantity = None; TargetNpcId = None; TargetSceneId = None; Completed = false; Progress = 0.0; Extra = [] }

/// TS `QuestRewardsSchema.items` item (inline object).
and QuestRewardItem =
    {
        ItemId: string
        Quantity: float
    }

    /// A `QuestRewardItem` with every field at its schema default.
    static member Default : QuestRewardItem =
        { ItemId = ""; Quantity = 0.0 }

and QuestRewards =
    {
        Money: float option
        Items: (QuestRewardItem list) option
        Experience: float option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `QuestRewards` with every field at its schema default.
    static member Default : QuestRewards =
        { Money = None; Items = None; Experience = None; Extra = [] }

and RecipeDefinition =
    {
        Id: string
        Name: string
        Inputs: RecipeIngredient list
        Outputs: RecipeIngredient list
        /// In-game minutes a machine needs; 0 = instant hand-craft. nonnegative.
        ProcessingMinutes: float
        /// Machine type required; absent = craftable by hand.
        MachineTypeId: string option
        /// Freeform crafting discipline for UI grouping ('cooking', 'magic', 'carpentry', …).
        Category: string
        /// Hand-craftable only while near a machine providing this station category.
        RequiresStationCategory: string option
        Unlock: RecipeUnlock option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `RecipeDefinition` with every field at its schema default.
    static member Default : RecipeDefinition =
        { Id = ""; Name = ""; Inputs = []; Outputs = []; ProcessingMinutes = 0.0; MachineTypeId = None; Category = "crafting"; RequiresStationCategory = None; Unlock = None; Extra = [] }

and RecipeIngredient =
    {
        ItemId: string
        /// int, positive.
        Quantity: float
    }

    /// A `RecipeIngredient` with every field at its schema default.
    static member Default : RecipeIngredient =
        { ItemId = ""; Quantity = 0.0 }

/// TS `RecipeUnlockSchema.skill` (inline object).
and RecipeSkillRequirement =
    {
        Skill: string
        /// int.
        Level: float
    }

    /// A `RecipeSkillRequirement` with every field at its schema default.
    static member Default : RecipeSkillRequirement =
        { Skill = ""; Level = 0.0 }

and RecipeUnlock =
    {
        /// Requires a skill at a minimum level.
        Skill: RecipeSkillRequirement option
        /// Requires a completed quest.
        QuestId: string option
        /// Only craftable in these seasons.
        Seasons: (string list) option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `RecipeUnlock` with every field at its schema default.
    static member Default : RecipeUnlock =
        { Skill = None; QuestId = None; Seasons = None; Extra = [] }

/// Serialized PRNG state — deterministic resume is a core guarantee.
and RngState =
    {
        /// Always `"xoshiro128ss"` (zod literal).
        Algorithm: string
        S: uint32 list
    }

    /// A `RngState` with every field at its schema default.
    static member Default : RngState =
        { Algorithm = "xoshiro128ss"; S = [ 0u; 0u; 0u; 0u ] }

and Scene =
    {
        Id: string
        Name: string
        /// int, positive.
        Width: float
        /// int, positive.
        Height: float
        /// Row-major: `Tiles[y][x]`.
        Tiles: (Tile list) list
        Transitions: SceneTransition list
        Npcs: string list
        Events: string list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Scene` with every field at its schema default.
    static member Default : Scene =
        { Id = ""; Name = ""; Width = 0.0; Height = 0.0; Tiles = []; Transitions = []; Npcs = []; Events = []; Extra = [] }

and SceneTransition =
    {
        FromX: float
        FromY: float
        ToSceneId: string
        ToX: float
        ToY: float
        /// Locked transitions don't fire; events can lock/unlock them (M3).
        Locked: bool option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `SceneTransition` with every field at its schema default.
    static member Default : SceneTransition =
        { FromX = 0.0; FromY = 0.0; ToSceneId = ""; ToX = 0.0; ToY = 0.0; Locked = None; Extra = [] }

and ShopDefinition =
    {
        Id: string
        Name: string
        Stock: ShopStockEntry list
        /// Multiplier applied to item base value when the player sells here.
        SellPriceMultiplier: float
        /// Whether this shop buys player items at all.
        BuysItems: bool
        /// Whether this shop repairs broken tools (for durability * repairCostPerPoint).
        RepairsTools: bool
        RepairCostPerPoint: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `ShopDefinition` with every field at its schema default.
    static member Default : ShopDefinition =
        { Id = ""; Name = ""; Stock = []; SellPriceMultiplier = 1.0; BuysItems = true; RepairsTools = false; RepairCostPerPoint = 0.5; Extra = [] }

/// A single line in a shop's stock list.
and ShopStockEntry =
    {
        ItemId: string
        /// Override price; defaults to the item's base value.
        Price: float option
        /// Restrict availability to these seasons (empty/absent = always).
        Seasons: (string list) option
        /// Max units purchasable per in-game day (absent = unlimited). int, positive.
        DailyLimit: float option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `ShopStockEntry` with every field at its schema default.
    static member Default : ShopStockEntry =
        { ItemId = ""; Price = None; Seasons = None; DailyLimit = None; Extra = [] }

and SkillState =
    {
        Xp: float
        /// int.
        Level: float
    }

    /// A `SkillState` with every field at its schema default.
    static member Default : SkillState =
        { Xp = 0.0; Level = 0.0 }

/// Sprite-sheet slicing metadata (M7). A sheet is a grid: columns = walk frames, rows = directions (down, left, right, up) when `directional`. Assets without this stay static images; games with zero art still render via the colored-rectangle fallback.
and SpriteSheet =
    {
        /// int, positive.
        FrameWidth: float
        /// int, positive.
        FrameHeight: float
        /// int, positive.
        Frames: float
        /// int, positive.
        TicksPerFrame: float
        Directional: bool
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `SpriteSheet` with every field at its schema default.
    static member Default : SpriteSheet =
        { FrameWidth = 0.0; FrameHeight = 0.0; Frames = 0.0; TicksPerFrame = 6.0; Directional = true; Extra = [] }

and Tile =
    {
        X: float
        Y: float
        /// One of `TileTypes`.
        Type: string
        /// Background layer: grass, soil, water, floor
        Background: string
        /// Overlay layer: path, rug, … renders on top of background
        Overlay: string option
        /// Object layer: wall, door, … renders on top of overlay
        Object: string option
        Crop: Crop option
        Item: Item option
        /// Gathering node instance (tree/rock/…) occupying this tile.
        Node: TileNode option
        /// Placed machine instance (M4 crafting).
        Machine: TileMachine option
        /// Mine ladder going down (M4 mining, generated floors).
        LadderDown: bool option
        Collision: bool
        CustomImage: string option
        Visuals: TileVisuals option
        /// One of `SoilStates`.
        SoilState: string option
        SoilMoisture: float
        SoilFertility: float
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `Tile` with every field at its schema default.
    static member Default : Tile =
        { X = 0.0; Y = 0.0; Type = ""; Background = ""; Overlay = None; Object = None; Crop = None; Item = None; Node = None; Machine = None; LadderDown = None; Collision = false; CustomImage = None; Visuals = None; SoilState = None; SoilMoisture = 0.0; SoilFertility = 0.0; Extra = [] }

/// Live machine instance on a tile.
and TileMachine =
    {
        TypeId: string
        /// In-flight job: recipe + absolute game-minute it completes.
        Processing: MachineProcessing option
        /// Finished output awaiting collection.
        Output: (RecipeIngredient list) option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `TileMachine` with every field at its schema default.
    static member Default : TileMachine =
        { TypeId = ""; Processing = None; Output = None; Extra = [] }

/// Live node instance state stored on a tile.
and TileNode =
    {
        TypeId: string
        /// int.
        RemainingHealth: float
        /// Set when depleted; used for respawn scheduling. int.
        DepletedOnDay: float option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `TileNode` with every field at its schema default.
    static member Default : TileNode =
        { TypeId = ""; RemainingHealth = 0.0; DepletedOnDay = None; Extra = [] }

/// TS `TileSchema.visuals` (inline object): per-layer art overrides.
and TileVisuals =
    {
        Background: VisualRef option
        Overlay: VisualRef option
        Object: VisualRef option
    }

    /// A `TileVisuals` with every field at its schema default.
    static member Default : TileVisuals =
        { Background = None; Overlay = None; Object = None }

and TimeConfig =
    {
        /// Minute-of-day the player wakes up (6:00). int.
        DayStartMinute: float
        /// Minute-of-day the player collapses if still awake (26:00 = 2am). int.
        DayEndMinute: float
        /// In-game minutes that pass per real-time second. positive.
        MinutesPerRealSecond: float
    }

    /// A `TimeConfig` with every field at its schema default.
    static member Default : TimeConfig =
        { DayStartMinute = 360.0; DayEndMinute = 1560.0; MinutesPerRealSecond = 1.0 }

and VisualRef =
    {
        AssetId: string
        Animation: string option
        Frame: ArtFrame option
    }

    /// A `VisualRef` with every field at its schema default.
    static member Default : VisualRef =
        { AssetId = ""; Animation = None; Frame = None }

and WeatherConfig =
    {
        Types: WeatherTypeDefinition list
        /// Per-season weighted roll tables (rolled at each day start).
        Table: (string * WeatherTableEntry list) list
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `WeatherConfig` with every field at its schema default.
    static member Default : WeatherConfig =
        { Types = []; Table = []; Extra = [] }

and WeatherTableEntry =
    {
        WeatherId: string
        /// positive.
        Weight: float
    }

    /// A `WeatherTableEntry` with every field at its schema default.
    static member Default : WeatherTableEntry =
        { WeatherId = ""; Weight = 0.0 }

and WeatherTypeDefinition =
    {
        Id: string
        Name: string
        /// Rain-like: outdoor soil is watered automatically at day start.
        WatersOutdoorSoil: bool
        /// Chance (0..1) each outdoor crop is destroyed overnight (storms).
        CropDamageChance: float
        /// NPCs with schedules stay home (skip schedule walking).
        NpcsStayInside: bool
        /// Renderer overlay hint: 'rain' | 'snow' | null.
        Overlay: string option
        /// Undeclared keys, in order (zod `.passthrough()`).
        Extra: (string * Json) list
    }

    /// A `WeatherTypeDefinition` with every field at its schema default.
    static member Default : WeatherTypeDefinition =
        { Id = ""; Name = ""; WatersOutdoorSoil = false; CropDamageChance = 0.0; NpcsStayInside = false; Overlay = None; Extra = [] }

/// Player entered a tile (or region when x2/y2 present).
and EnterTileCondition =
    {
        X: float
        Y: float
        X2: float option
        Y2: float option
    }

    static member Default : EnterTileCondition =
        { X = 0.0; Y = 0.0; X2 = None; Y2 = None }

/// Player interacted while facing a tile (or region).
and InteractTileCondition =
    {
        X: float
        Y: float
        X2: float option
        Y2: float option
    }

    static member Default : InteractTileCondition =
        { X = 0.0; Y = 0.0; X2 = None; Y2 = None }

and HasItemCondition =
    {
        ItemId: string
        Quantity: float
    }

    static member Default : HasItemCondition =
        { ItemId = ""; Quantity = 1.0 }

and InventorySpaceCondition =
    {
        ItemId: string
        /// int, positive.
        Quantity: float
    }

    static member Default : InventorySpaceCondition =
        { ItemId = ""; Quantity = 1.0 }

and FlagCondition =
    {
        Flag: string
        Value: bool
    }

    static member Default : FlagCondition =
        { Flag = ""; Value = true }

and DayRangeCondition =
    {
        MinDay: float option
        MaxDay: float option
    }

    static member Default : DayRangeCondition =
        { MinDay = None; MaxDay = None }

and SeasonCondition =
    {
        Seasons: string list
    }

    static member Default : SeasonCondition =
        { Seasons = [] }

and YearRangeCondition =
    {
        MinYear: float option
        MaxYear: float option
    }

    static member Default : YearRangeCondition =
        { MinYear = None; MaxYear = None }

and TimeOfDayCondition =
    {
        MinMinute: float
        MaxMinute: float
    }

    static member Default : TimeOfDayCondition =
        { MinMinute = 0.0; MaxMinute = 0.0 }

and QuestStatusCondition =
    {
        QuestId: string
        /// One of `QuestStatuses`.
        Status: string
    }

    static member Default : QuestStatusCondition =
        { QuestId = ""; Status = "" }

/// Friendship threshold with an NPC (M4 heart events).
and FriendshipCondition =
    {
        NpcId: string
        Min: float
    }

    static member Default : FriendshipCondition =
        { NpcId = ""; Min = 0.0 }

/// Current weather (M4).
and WeatherCondition =
    {
        WeatherIds: string list
    }

    static member Default : WeatherCondition =
        { WeatherIds = [] }

/// Today is the named festival (M9 calendar).
and FestivalIdCondition =
    {
        FestivalId: string
    }

    static member Default : FestivalIdCondition =
        { FestivalId = "" }

/// TS `EventConditionSchema`: a union on `type`.
and [<RequireQualifiedAccess>] EventCondition =
    | EnterTile of EnterTileCondition
    | InteractTile of InteractTileCondition
    | HasItem of HasItemCondition
    | InventorySpace of InventorySpaceCondition
    | Flag of FlagCondition
    | DayRange of DayRangeCondition
    | Season of SeasonCondition
    | YearRange of YearRangeCondition
    | TimeOfDay of TimeOfDayCondition
    | QuestStatus of QuestStatusCondition
    | Friendship of FriendshipCondition
    | Weather of WeatherCondition
    | FestivalId of FestivalIdCondition

    /// The `type` discriminator.
    member this.Type =
        match this with
        | EnterTile _ -> "enterTile"
        | InteractTile _ -> "interactTile"
        | HasItem _ -> "hasItem"
        | InventorySpace _ -> "inventorySpace"
        | Flag _ -> "flag"
        | DayRange _ -> "dayRange"
        | Season _ -> "season"
        | YearRange _ -> "yearRange"
        | TimeOfDay _ -> "timeOfDay"
        | QuestStatus _ -> "questStatus"
        | Friendship _ -> "friendship"
        | Weather _ -> "weather"
        | FestivalId _ -> "festivalId"
