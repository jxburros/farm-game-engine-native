//! Project → content/state bridge (port of `State.cs` / state.ts). [`crate::EngineContext`]
//! itself lives in `engine_types.rs`.

use crate::content_builtin;
use crate::farming::crops;
use crate::game_time;
use crate::packs;
use crate::rng;
use crate::schema::{
    center_coordinate, default_weather_config, CalendarConfig, ClockState, GameContent, GameProject, GameState,
    GameStateMeta, KeptState, MineProgress, MoveIntent, NpcState, PlayerState, ProjectSettings, QuestObjectiveProgress,
    QuestProgress, WeatherConfig, WorldState, CURRENT_CONTENT_VERSION, CURRENT_SAVE_VERSION,
};
use crate::start::StartState;
use crate::units;
use indexmap::{IndexMap, IndexSet};
use serde_json::Value;

/// Content derived from the project's own fields (built-in fallbacks + authored content) — before
/// packs layer on top.
pub fn create_base_content_from_project(project: &GameProject) -> GameContent {
    let settings = resolve_settings(&project.settings);
    let node_type_ids: IndexSet<&str> = project.node_types.iter().map(|definition| definition.id.as_str()).collect();
    // Built-in node types are always available; project definitions override by id.
    let mut node_types = Vec::new();
    node_types
        .extend(content_builtin::default_node_types().into_iter().filter(|d| !node_type_ids.contains(d.id.as_str())));
    node_types
        .extend(content_builtin::mine_node_types().into_iter().filter(|d| !node_type_ids.contains(d.id.as_str())));
    node_types.extend(project.node_types.iter().cloned());

    GameContent {
        content_version: CURRENT_CONTENT_VERSION,
        crops: crops::merge_custom_crop_definitions(project.custom_crops.as_deref().unwrap_or(&[])),
        items: if project.items.is_empty() { content_builtin::create_default_items() } else { project.items.clone() },
        npcs: project.npcs.clone(),
        dialogues: project.dialogues.clone(),
        quests: project.quests.clone(),
        events: project.events.clone(),
        shops: project.shops.clone(),
        node_types,
        settings,
        recipes: project.recipes.clone(),
        machine_types: project.machine_types.clone(),
        // WeatherConfigSchema.safeParse(...) ? parse(...) : parse(defaultWeatherConfig())
        weather: if is_valid_weather_config(&project.weather) {
            project.weather.clone()
        } else {
            default_weather_config()
        },
        animal_species: project.animal_species.clone(),
        fish_tables: project.fish_tables.clone(),
        // MineConfigSchema.parse(project.mine ?? { enabled: false }); the defaults carry the zod ones.
        mine: project.mine.clone(),
        actions: project.actions.clone(),
        minigames: project.minigames.clone(),
        scenes: project.scenes.clone(),
        start_scene_id: project.start_scene_id.clone(),
    }
}

/// Derive the immutable content view from an editor project: base content, then enabled content
/// packs layered on top (M5) with explicit override semantics.
pub fn create_content_from_project(project: &GameProject) -> GameContent {
    let merged =
        packs::merge_packs_into_content(&create_base_content_from_project(project), &project.content_packs).content;
    // Localized game text from pack string tables (M7); authored text is the fallback.
    let locale = merged.settings.locale.clone();
    packs::apply_locale_strings(merged, &project.content_packs, &locale)
}

/// Create a running GameState from a project. The world starts as a deep copy of the project's
/// scenes (which carry any in-progress crops from previous play sessions — historical behavior
/// preserved until the M3 playtest sandbox separates them).
///
/// `seed` is TS `options.seed`.
pub fn create_game_state(project: &GameProject, seed: Option<&str>) -> GameState {
    create_game_state_from_start(&StartState::from_project(project), seed)
}

/// [`create_game_state`] from the new-game inputs a cartridge carries (see [`crate::start`]).
pub fn create_game_state_from_start(start: &StartState, seed: Option<&str>) -> GameState {
    let mut quests = IndexMap::new();
    for quest in &start.quests {
        let mut objectives = IndexMap::new();
        for objective in &quest.objectives {
            objectives.insert(
                objective.id.clone(),
                QuestObjectiveProgress { progress: objective.progress, completed: objective.completed },
            );
        }
        quests.insert(quest.id.clone(), QuestProgress { status: quest.status.clone(), objectives: Some(objectives) });
    }

    let mut npcs = IndexMap::new();
    for npc in &start.npcs {
        npcs.insert(
            npc.id.clone(),
            NpcState { x: npc.x, y: npc.y, scene_id: npc.scene_id.clone(), ..NpcState::default() },
        );
    }

    let resolved_settings = resolve_settings(&start.settings);
    let max_energy = start.player.max_energy.unwrap_or(resolved_settings.max_energy);
    let engine_seed = match seed {
        Some(seed) => seed.to_owned(),
        None => format!("{}:{}", start.id, units::format_number(start.game_start_time)),
    };

    let flags: IndexMap<String, Value> =
        start.event_flags.iter().map(|(key, value)| (key.clone(), units::canonical_json(value.clone()))).collect();

    let mut clock = ClockState {
        tick: 0,
        // TS `project.currentTimeMinutes ?? dayStartMinute` / `currentYear ?? 1`: both are
        // required (non-nullable) project fields here, so the fallbacks never apply.
        time_minutes: start.current_time_minutes,
        day: start.current_day,
        season: start.current_season.clone(),
        day_of_season: start.current_day_of_season.unwrap_or(0),
        year: start.current_year,
        weather_id: start.current_weather_id.clone().unwrap_or_else(|| "sun".to_owned()),
    };
    game_time::reconcile_clock(&resolved_settings.calendar, &mut clock);

    let state = GameState {
        meta: GameStateMeta {
            save_version: CURRENT_SAVE_VERSION,
            engine_seed: engine_seed.clone(),
            packs: start.packs.clone(),
        },
        clock,
        world: WorldState { scenes: start.scenes.clone() },
        player: PlayerState {
            // Projects may store tile indices (legacy/authored) or fractional free-movement
            // positions; tile indices land on the tile center.
            x: center_coordinate(start.player.x),
            y: center_coordinate(start.player.y),
            move_intent: MoveIntent { dx: 0, dy: 0 },
            direction: start.player.direction.clone(),
            scene_id: start.player.scene_id.clone(),
            inventory: start.player.inventory.clone(),
            max_inventory_size: start.player.max_inventory_size,
            money: start.player.money,
            energy: start.player.energy.unwrap_or(max_energy),
            max_energy,
            skills: start.player.skills.clone().unwrap_or_default(),
            active_quests: start.player.active_quests.clone(),
            completed_quests: start.player.completed_quests.clone(),
            equipped_tool: start.player.equipped_tool.clone(),
        },
        npcs,
        quests,
        dialogue: None,
        shop: None,
        minigame: None,
        shop_purchases_today: IndexMap::new(),
        social: start.social_state.clone().unwrap_or_default(),
        animals: start.animals.clone(),
        mine: MineProgress { deepest_floor: start.mine_deepest_floor.unwrap_or(0), current_floor: 0 },
        flags,
        quarantined_items: start.quarantined_items.clone().unwrap_or_default(),
        // An all-zero state would draw 0 forever (#140): seed it like a project without one.
        rng: start
            .rng_state
            .clone()
            .filter(|rng| !rng.is_degenerate())
            .unwrap_or_else(|| rng::create_rng_state(&engine_seed)),
    };

    // Items from missing/disabled packs are quarantined, not dropped; they come back when the
    // pack does.
    let enabled_packs: IndexSet<String> = start.packs.iter().map(|pack| pack.id.clone()).collect();
    let mut state = packs::reconcile_pack_items(state, &enabled_packs);
    // What a kept playtest left open or mid-way (#36).
    if let Some(kept) = &start.kept_state {
        kept.apply_to(&mut state);
    }
    state
}

/// Write a running GameState back into the project (persistence bridge — keeps the single
/// project store and the editor views in sync while the engine owns play-mode rules).
pub fn apply_state_to_project(project: &GameProject, state: &GameState) -> GameProject {
    let mut next = project.clone();
    // Generated scenes (mine floors) sync through so rendering works while the player stands in
    // one; they are dropped again on exitMine and are hidden from editor scene lists
    // (scene.generated flag).
    next.scenes = state.world.scenes.clone();
    for npc in &mut next.npcs {
        if let Some(npc_state) = state.npcs.get(&npc.id) {
            npc.x = npc_state.x;
            npc.y = npc_state.y;
            npc.scene_id = npc_state.scene_id.clone();
        }
    }
    for quest in &mut next.quests {
        if let Some(progress) = state.quests.get(&quest.id) {
            quest.status = progress.status.clone();
            for objective in &mut quest.objectives {
                if let Some(objective_progress) = progress.objectives.as_ref().and_then(|map| map.get(&objective.id)) {
                    objective.progress = objective_progress.progress;
                    objective.completed = objective_progress.completed;
                }
            }
        }
    }
    next.player.x = state.player.x;
    next.player.y = state.player.y;
    next.player.direction = state.player.direction.clone();
    next.player.scene_id = state.player.scene_id.clone();
    next.player.inventory = state.player.inventory.clone();
    next.player.max_inventory_size = state.player.max_inventory_size;
    next.player.money = state.player.money;
    next.player.energy = Some(state.player.energy);
    next.player.max_energy = Some(state.player.max_energy);
    next.player.skills = Some(state.player.skills.clone());
    next.player.active_quests = state.player.active_quests.clone();
    next.player.completed_quests = state.player.completed_quests.clone();
    next.player.equipped_tool = state.player.equipped_tool.clone();
    // Flag values go back as they are: plugins store numbers and strings (#36).
    next.event_flags = state.flags.clone();
    next.animals = state.animals.clone();
    next.social_state = Some(state.social.clone());
    next.quarantined_items = Some(state.quarantined_items.clone());
    next.current_weather_id = Some(state.clock.weather_id.clone());
    next.mine_deepest_floor = Some(state.mine.deepest_floor);
    next.current_day = state.clock.day;
    next.current_season = state.clock.season.clone();
    next.current_day_of_season = kept_day_of_season(&resolve_settings(&project.settings).calendar, &state.clock);
    next.current_time_minutes = state.clock.time_minutes;
    next.current_year = state.clock.year;
    next.rng_state = Some(state.rng.clone());
    next.kept_state = KeptState::of_state(state);
    next
}

/// The `currentDayOfSeason` a project keeps for `clock`: none when `currentDay` alone lands on
/// the clock's season and day of season (a game that started on day 1 of the first season).
fn kept_day_of_season(calendar: &CalendarConfig, clock: &ClockState) -> Option<u32> {
    let date = game_time::clock_date(calendar, clock);
    let natural = game_time::natural_date(calendar, clock.day);
    (natural.season.id != clock.season || natural.day_of_season != date.day_of_season).then_some(date.day_of_season)
}

/// Top-level project keys [`apply_state_to_project`] writes.
const SYNCED_PROJECT_KEYS: [&str; 14] = [
    "scenes",
    "animals",
    "eventFlags",
    "socialState",
    "quarantinedItems",
    "currentWeatherId",
    "mineDeepestFloor",
    "currentDay",
    "currentSeason",
    "currentDayOfSeason",
    "currentTimeMinutes",
    "currentYear",
    "rngState",
    "keptState",
];

/// Player keys [`apply_state_to_project`] writes.
const SYNCED_PLAYER_KEYS: [&str; 13] = [
    "x",
    "y",
    "direction",
    "sceneId",
    "inventory",
    "maxInventorySize",
    "money",
    "energy",
    "maxEnergy",
    "skills",
    "activeQuests",
    "completedQuests",
    "equippedTool",
];

/// [`apply_state_to_project`] on the project's JSON: the keys the state writes back come from
/// the state, every other value stays exactly as the project JSON has it. Reading a project into
/// [`GameProject`] quantizes its content (a chance of 0.01 becomes the nearest 2⁻³² step), and
/// "keep changes" must not rewrite what the creator typed (docs/NUMERICS.md "Project
/// migration").
pub fn apply_state_to_project_json(project: &Value, state: &GameState) -> Result<Value, String> {
    let typed: GameProject = serde_json::from_value(project.clone()).map_err(|e| format!("project JSON: {e}"))?;
    let synced = serde_json::to_value(apply_state_to_project(&typed, state)).map_err(|e| e.to_string())?;
    let mut next = project.clone();
    let (Some(out), Some(synced)) = (next.as_object_mut(), synced.as_object()) else {
        return Err("project JSON: expected an object".to_owned());
    };
    copy_keys(out, synced, &SYNCED_PROJECT_KEYS);
    if let (Some(Value::Object(player)), Some(Value::Object(synced_player))) =
        (out.get_mut("player"), synced.get("player"))
    {
        copy_keys(player, synced_player, &SYNCED_PLAYER_KEYS);
    }
    // NPCs and quests keep their order (the typed write-back edits them in place).
    for_each_pair(out.get_mut("npcs"), synced.get("npcs"), |npc, synced_npc| {
        copy_keys(npc, synced_npc, &["x", "y", "sceneId"]);
    });
    for_each_pair(out.get_mut("quests"), synced.get("quests"), |quest, synced_quest| {
        copy_keys(quest, synced_quest, &["status"]);
        for_each_pair(quest.get_mut("objectives"), synced_quest.get("objectives"), |objective, synced_objective| {
            copy_keys(objective, synced_objective, &["progress", "completed"]);
        });
    });
    Ok(next)
}

/// Sets `keys` of `out` to their values in `from` (removing those `from` leaves out). Keys keep
/// their place: a removal shifts the later keys up rather than moving the last key into the gap
/// (`Map::remove` is `swap_remove` under `preserve_order`), so Keep changes leaves no noise in
/// project diffs (#142).
fn copy_keys(out: &mut serde_json::Map<String, Value>, from: &serde_json::Map<String, Value>, keys: &[&str]) {
    for key in keys {
        match from.get(*key) {
            Some(value) => {
                out.insert((*key).to_owned(), value.clone());
            }
            None => {
                out.shift_remove(*key);
            }
        }
    }
}

/// Calls `f` on the objects at the same index of two JSON arrays.
fn for_each_pair(
    out: Option<&mut Value>,
    from: Option<&Value>,
    mut f: impl FnMut(&mut serde_json::Map<String, Value>, &serde_json::Map<String, Value>),
) {
    let (Some(Value::Array(out)), Some(Value::Array(from))) = (out, from) else {
        return;
    };
    for (item, from_item) in out.iter_mut().zip(from) {
        if let (Value::Object(item), Value::Object(from_item)) = (item, from_item) {
            f(item, from_item);
        }
    }
}

/// The settings a game runs: `settings` with each part [`settings_fallbacks`] names replaced by
/// its default (an invalid time window or clock rate takes the default time settings), and
/// calendar seasons and festivals with no days dropped. The TS `ProjectSettingsSchema.safeParse`
/// replaced every setting when one was invalid, so one festival on day 0 lost the whole
/// calendar, energy and locale (#140). F# `ContentCompiler.settings` does the same, and
/// Problems reports each replaced value as an error.
pub fn resolve_settings(settings: &ProjectSettings) -> ProjectSettings {
    let defaults = ProjectSettings::default();
    let mut resolved = settings.clone();
    if settings.movement.player_speed <= 0 {
        resolved.movement = defaults.movement;
    }
    if settings.max_energy <= 0 {
        resolved.max_energy = defaults.max_energy;
    }
    // `.min(0).max(1)` in thousandths.
    if settings.collapse_energy_fraction > units::MILLI_ONE {
        resolved.collapse_energy_fraction = defaults.collapse_energy_fraction;
    }
    if settings.collapse_money_penalty < 0 {
        resolved.collapse_money_penalty = defaults.collapse_money_penalty;
    }
    if !game_time::is_valid_time_config(&settings.time) {
        resolved.time = defaults.time;
    }
    resolved.calendar.seasons.retain(|season| season.days > 0);
    resolved.calendar.festivals.retain(|festival| festival.day > 0);
    resolved
}

/// The settings [`resolve_settings`] replaces or drops, as `settings.*` paths (empty when every
/// setting is used as written).
pub fn settings_fallbacks(s: &ProjectSettings) -> Vec<String> {
    let mut out = Vec::new();
    if s.movement.player_speed <= 0 {
        out.push("settings.movement".to_owned());
    }
    if s.max_energy <= 0 {
        out.push("settings.maxEnergy".to_owned());
    }
    if s.collapse_energy_fraction > units::MILLI_ONE {
        out.push("settings.collapseEnergyFraction".to_owned());
    }
    if s.collapse_money_penalty < 0 {
        out.push("settings.collapseMoneyPenalty".to_owned());
    }
    if !game_time::is_valid_time_config(&s.time) {
        out.push("settings.time".to_owned());
    }
    for (index, season) in s.calendar.seasons.iter().enumerate() {
        if season.days == 0 {
            out.push(format!("settings.calendar.seasons.{index}"));
        }
    }
    for (index, festival) in s.calendar.festivals.iter().enumerate() {
        if festival.day == 0 {
            out.push(format!("settings.calendar.festivals.{index}"));
        }
    }
    out
}

// zod refinements on the settings numbers. The values are already on their integer grids
// (whole minutes and days, thousandths), so `.int()` and "is a number" hold by construction;
// what is left are the sign and range checks.

/// TS `ProjectSettingsSchema.safeParse(settings).success`, plus the day window and clock rate
/// checks of [`game_time::is_valid_time_config`]: every setting is used as written.
pub fn is_valid_settings(s: &ProjectSettings) -> bool {
    settings_fallbacks(s).is_empty()
}

/// TS `WeatherConfigSchema.safeParse(config).success`: chances lie in 0–1 by construction, so
/// what is left is that every weight is positive.
pub fn is_valid_weather_config(config: &WeatherConfig) -> bool {
    config.table.values().flatten().all(|entry| entry.weight > 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::{CalendarSeason, WeatherTableEntry};

    #[test]
    fn settings_validation_mirrors_the_zod_refinements() {
        assert!(is_valid_settings(&ProjectSettings::default()));
        let mut settings = ProjectSettings::default();
        settings.movement.player_speed = 0;
        assert!(!is_valid_settings(&settings));
        assert_eq!(resolve_settings(&settings), ProjectSettings::default());

        let settings = ProjectSettings { collapse_energy_fraction: 1500, ..ProjectSettings::default() };
        assert!(!is_valid_settings(&settings));

        let settings = ProjectSettings { max_energy: 0, ..ProjectSettings::default() };
        assert!(!is_valid_settings(&settings));

        let mut settings = ProjectSettings::default();
        settings.time.minutes_per_real_second = 0;
        assert!(!is_valid_settings(&settings));

        let settings = ProjectSettings { collapse_money_penalty: -1, ..ProjectSettings::default() };
        assert!(!is_valid_settings(&settings));

        let mut settings = ProjectSettings::default();
        settings.calendar.seasons.push(CalendarSeason { id: "mud".to_owned(), name: "Mud".to_owned(), days: 0 });
        assert!(!is_valid_settings(&settings));

        let settings = ProjectSettings { max_energy: i32::MAX, ..ProjectSettings::default() };
        assert!(is_valid_settings(&settings));
    }

    #[test]
    fn weather_validation_mirrors_the_zod_refinements() {
        assert!(is_valid_weather_config(&WeatherConfig::default()));
        assert!(is_valid_weather_config(&default_weather_config()));

        let mut config = default_weather_config();
        config.table.insert("mud".to_owned(), vec![WeatherTableEntry { weather_id: "sun".to_owned(), weight: 0 }]);
        assert!(!is_valid_weather_config(&config));
    }

    #[test]
    fn engine_seed_defaults_to_project_id_and_start_time() {
        let project =
            GameProject { id: "p1".to_owned(), game_start_time: 1_500_000_000_000.0, ..GameProject::default() };
        let state = create_game_state(&project, None);
        assert_eq!(state.meta.engine_seed, "p1:1500000000000");
        assert_eq!(state.rng, rng::create_rng_state("p1:1500000000000"));
        let seeded = create_game_state(&project, Some("seed-x"));
        assert_eq!(seeded.meta.engine_seed, "seed-x");
        assert_eq!(seeded.player.x, units::tile_center(0));
        assert_eq!(seeded.player.energy, units::points(100));
    }
}
