//! Port of `Primitives.cs` (packages/engine-schemas/src/primitives.ts).
//!
//! Primitive enums shared by content, project and save schemas. zod string enums stay plain
//! `String`s (content is open-ended and JSON must round-trip); each enum gets a module of
//! constants plus `ALL`.

/// TS `TileTypeSchema`.
pub mod tile_types {
    pub const GRASS: &str = "grass";
    pub const SOIL: &str = "soil";
    pub const WATER: &str = "water";
    pub const PATH: &str = "path";
    pub const WALL: &str = "wall";
    pub const DOOR: &str = "door";
    pub const FLOOR: &str = "floor";

    pub const ALL: &[&str] = &[GRASS, SOIL, WATER, PATH, WALL, DOOR, FLOOR];
}

/// TS `DirectionSchema`.
pub mod directions {
    pub const UP: &str = "up";
    pub const DOWN: &str = "down";
    pub const LEFT: &str = "left";
    pub const RIGHT: &str = "right";

    pub const ALL: &[&str] = &[UP, DOWN, LEFT, RIGHT];
}

/// TS `ItemTypeSchema`.
pub mod item_types {
    pub const SEED: &str = "seed";
    pub const TOOL: &str = "tool";
    pub const CROP: &str = "crop";
    pub const GIFT: &str = "gift";
    pub const QUEST: &str = "quest";
    pub const FERTILIZER: &str = "fertilizer";
    pub const MATERIAL: &str = "material";
    pub const FISH: &str = "fish";

    pub const ALL: &[&str] = &[SEED, TOOL, CROP, GIFT, QUEST, FERTILIZER, MATERIAL, FISH];
}

/// TS `ToolTypeSchema`.
pub mod tool_types {
    pub const WATERING_CAN: &str = "watering-can";
    pub const HOE: &str = "hoe";
    pub const AXE: &str = "axe";
    pub const PICKAXE: &str = "pickaxe";
    pub const SCYTHE: &str = "scythe";
    pub const FISHING_ROD: &str = "fishing-rod";

    pub const ALL: &[&str] = &[WATERING_CAN, HOE, AXE, PICKAXE, SCYTHE, FISHING_ROD];
}

/// TS `EditorModeSchema`.
pub mod editor_modes {
    pub const TILES: &str = "tiles";
    pub const NPCS: &str = "npcs";
    pub const ITEMS: &str = "items";
    pub const EVENTS: &str = "events";
    pub const PLAY: &str = "play";
    pub const QUESTS: &str = "quests";

    pub const ALL: &[&str] = &[TILES, NPCS, ITEMS, EVENTS, PLAY, QUESTS];
}

/// TS `QuestStatusSchema`.
pub mod quest_statuses {
    pub const NOT_STARTED: &str = "not-started";
    pub const ACTIVE: &str = "active";
    pub const COMPLETED: &str = "completed";
    pub const FAILED: &str = "failed";

    pub const ALL: &[&str] = &[NOT_STARTED, ACTIVE, COMPLETED, FAILED];
}

/// TS `QuestObjectiveTypeSchema`.
pub mod quest_objective_types {
    pub const COLLECT: &str = "collect";
    pub const HARVEST: &str = "harvest";
    pub const TALK: &str = "talk";
    pub const VISIT: &str = "visit";
    pub const CRAFT: &str = "craft";
    pub const GIFT: &str = "gift";

    pub const ALL: &[&str] = &[COLLECT, HARVEST, TALK, VISIT, CRAFT, GIFT];
}

/// TS `SoilStateSchema`.
pub mod soil_states {
    pub const DRY: &str = "dry";
    pub const WATERED: &str = "watered";
    pub const FERTILIZED: &str = "fertilized";
    pub const TILLED: &str = "tilled";

    pub const ALL: &[&str] = &[DRY, WATERED, FERTILIZED, TILLED];
}

/// TS `CropQualitySchema`.
pub mod crop_qualities {
    pub const NORMAL: &str = "normal";
    pub const SILVER: &str = "silver";
    pub const GOLD: &str = "gold";
    pub const IRIDIUM: &str = "iridium";

    pub const ALL: &[&str] = &[NORMAL, SILVER, GOLD, IRIDIUM];
}

/// TS `CropMutationSchema`: one of these or `null` (so fields holding it are `Option<String>`
/// written as `null`).
pub mod crop_mutations {
    pub const GIANT: &str = "giant";
    pub const GOLDEN: &str = "golden";
    pub const ANCIENT: &str = "ancient";

    pub const ALL: &[&str] = &[GIANT, GOLDEN, ANCIENT];
}

/// Seasons are open strings (creator-defined calendars, M9) so custom season ids validate the
/// same way custom crops do. `CLASSIC_SEASONS` enumerates the four seasons the built-in calendar
/// ships with, for defaults/editors.
pub const CLASSIC_SEASONS: &[&str] = &["spring", "summer", "fall", "winter"];

/// Crop identifiers are open strings so that custom/modded crops share the same pipeline as
/// built-in ones. `BUILTIN_CROP_IDS` enumerates the crops shipped with the default content pack.
pub const BUILTIN_CROP_IDS: &[&str] =
    &["wheat", "corn", "tomato", "carrot", "potato", "strawberry", "pumpkin", "cauliflower", "blueberry"];
