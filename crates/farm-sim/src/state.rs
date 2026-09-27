//! Project → content/state bridge (port of `State.cs` / state.ts). [`crate::EngineContext`]
//! itself lives in `engine_types.rs`.

use crate::content_builtin;
use crate::farming::crops;
use crate::js;
use crate::packs;
use crate::rng;
use crate::schema::{
    center_coordinate, default_weather_config, ClockState, GameContent, GameProject, GameState, GameStateMeta,
    MineProgress, MoveIntent, NpcState, PlayerState, ProjectSettings, QuestObjectiveProgress, QuestProgress,
    WeatherConfig, WorldState, CURRENT_CONTENT_VERSION, CURRENT_SAVE_VERSION,
};
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
    let mut quests = IndexMap::new();
    for quest in &project.quests {
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
    for npc in &project.npcs {
        npcs.insert(
            npc.id.clone(),
            NpcState { x: npc.x, y: npc.y, scene_id: npc.scene_id.clone(), ..NpcState::default() },
        );
    }

    let resolved_settings = resolve_settings(&project.settings);
    let max_energy = project.player.max_energy.unwrap_or(resolved_settings.max_energy);
    let engine_seed = match seed {
        Some(seed) => seed.to_owned(),
        None => format!("{}:{}", project.id, js::num(project.game_start_time)),
    };

    let flags: IndexMap<String, Value> =
        project.event_flags.iter().map(|(key, value)| (key.clone(), Value::Bool(*value))).collect();

    let state = GameState {
        meta: GameStateMeta {
            save_version: CURRENT_SAVE_VERSION,
            engine_seed: engine_seed.clone(),
            packs: packs::stamp_packs(&project.content_packs),
        },
        clock: ClockState {
            tick: 0.0,
            // TS `project.currentTimeMinutes ?? dayStartMinute` / `currentYear ?? 1`: both are
            // required (non-nullable) project fields here, so the fallbacks never apply.
            time_minutes: project.current_time_minutes,
            day: project.current_day,
            season: project.current_season.clone(),
            year: project.current_year,
            weather_id: project.current_weather_id.clone().unwrap_or_else(|| "sun".to_owned()),
        },
        world: WorldState { scenes: project.scenes.clone() },
        player: PlayerState {
            // Projects may store tile indices (legacy/authored) or fractional free-movement
            // positions; tile indices land on the tile center.
            x: center_coordinate(project.player.x),
            y: center_coordinate(project.player.y),
            move_intent: MoveIntent { dx: 0.0, dy: 0.0 },
            direction: project.player.direction.clone(),
            scene_id: project.player.scene_id.clone(),
            inventory: project.player.inventory.clone(),
            max_inventory_size: project.player.max_inventory_size,
            money: project.player.money,
            energy: project.player.energy.unwrap_or(max_energy),
            max_energy,
            skills: project.player.skills.clone().unwrap_or_default(),
            active_quests: project.player.active_quests.clone(),
            completed_quests: project.player.completed_quests.clone(),
            equipped_tool: project.player.equipped_tool.clone(),
        },
        npcs,
        quests,
        dialogue: None,
        shop: None,
        minigame: None,
        shop_purchases_today: IndexMap::new(),
        social: project.social_state.clone().unwrap_or_default(),
        animals: project.animals.clone(),
        mine: MineProgress { deepest_floor: project.mine_deepest_floor.unwrap_or(0.0), current_floor: 0.0 },
        flags,
        quarantined_items: project.quarantined_items.clone().unwrap_or_default(),
        rng: project.rng_state.clone().unwrap_or_else(|| rng::create_rng_state(&engine_seed)),
    };

    // Items from missing/disabled packs are quarantined, not dropped; they come back when the
    // pack does.
    let enabled_packs: IndexSet<String> = project
        .content_packs
        .iter()
        .filter(|install| install.enabled)
        .map(|install| install.pack.manifest.id.clone())
        .collect();
    packs::reconcile_pack_items(state, &enabled_packs)
}

/// Write a running GameState back into the project (persistence bridge — keeps the single
/// project store and the editor views in sync while the engine owns play-mode rules).
pub fn apply_state_to_project(project: &GameProject, state: &GameState) -> GameProject {
    let event_flags: IndexMap<String, bool> =
        state.flags.iter().map(|(key, value)| (key.clone(), js::truthy(Some(value)))).collect();

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
    next.event_flags = event_flags;
    next.animals = state.animals.clone();
    next.social_state = Some(state.social.clone());
    next.quarantined_items = Some(state.quarantined_items.clone());
    next.current_weather_id = Some(state.clock.weather_id.clone());
    next.mine_deepest_floor = Some(state.mine.deepest_floor);
    next.current_day = state.clock.day;
    next.current_season = state.clock.season.clone();
    next.current_time_minutes = state.clock.time_minutes;
    next.current_year = state.clock.year;
    next.rng_state = Some(state.rng.clone());
    next
}

/// TS `ProjectSettingsSchema.safeParse(project.settings ?? {})`: the typed settings when they
/// satisfy the schema's refinements, otherwise `DEFAULT_PROJECT_SETTINGS`.
pub fn resolve_settings(settings: &ProjectSettings) -> ProjectSettings {
    if is_valid_settings(settings) {
        settings.clone()
    } else {
        ProjectSettings::default()
    }
}

// zod refinements on `z.number()`. Each comparison is false for NaN, which is what rejects it
// (`z.number()` refuses NaN but accepts ±Infinity).

/// `z.number()`.
fn is_number(value: f64) -> bool {
    !value.is_nan()
}

/// `z.number().int()`.
fn is_int(value: f64) -> bool {
    js::is_integer(value)
}

/// `.positive()`.
fn positive(value: f64) -> bool {
    value > 0.0
}

/// `.nonnegative()`.
fn nonnegative(value: f64) -> bool {
    value >= 0.0
}

/// `.min(0).max(1)`.
fn unit_fraction(value: f64) -> bool {
    (0.0..=1.0).contains(&value)
}

/// TS `ProjectSettingsSchema.safeParse(settings).success`.
pub fn is_valid_settings(s: &ProjectSettings) -> bool {
    if !is_number(s.movement.player_speed) || !positive(s.movement.player_speed) {
        return false;
    }
    if !positive(s.max_energy) {
        return false;
    }
    if !unit_fraction(s.collapse_energy_fraction) {
        return false;
    }
    if !nonnegative(s.collapse_money_penalty) {
        return false;
    }
    if !is_int(s.time.day_start_minute) || !is_int(s.time.day_end_minute) {
        return false;
    }
    if !positive(s.time.minutes_per_real_second) {
        return false;
    }
    for season in &s.calendar.seasons {
        if !is_int(season.days) || !positive(season.days) {
            return false;
        }
    }
    for festival in &s.calendar.festivals {
        if !is_int(festival.day) || !positive(festival.day) {
            return false;
        }
    }
    if s.skill_level_curve.iter().any(|value| !is_number(*value)) {
        return false;
    }
    true
}

/// TS `WeatherConfigSchema.safeParse(config).success`.
pub fn is_valid_weather_config(config: &WeatherConfig) -> bool {
    for weather_type in &config.types {
        if !unit_fraction(weather_type.crop_damage_chance) {
            return false;
        }
    }
    for entries in config.table.values() {
        for entry in entries {
            if !positive(entry.weight) {
                return false;
            }
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::{CalendarSeason, WeatherTableEntry, WeatherTypeDefinition};

    #[test]
    fn settings_validation_mirrors_the_zod_refinements() {
        assert!(is_valid_settings(&ProjectSettings::default()));
        let mut settings = ProjectSettings::default();
        settings.movement.player_speed = 0.0;
        assert!(!is_valid_settings(&settings));
        assert_eq!(resolve_settings(&settings), ProjectSettings::default());

        let settings = ProjectSettings { collapse_energy_fraction: 1.5, ..ProjectSettings::default() };
        assert!(!is_valid_settings(&settings));

        let settings = ProjectSettings { max_energy: f64::NAN, ..ProjectSettings::default() };
        assert!(!is_valid_settings(&settings));

        let mut settings = ProjectSettings::default();
        settings.time.day_start_minute = 360.5;
        assert!(!is_valid_settings(&settings));

        let mut settings = ProjectSettings::default();
        settings.calendar.seasons.push(CalendarSeason { id: "mud".to_owned(), name: "Mud".to_owned(), days: 0.0 });
        assert!(!is_valid_settings(&settings));

        let mut settings = ProjectSettings::default();
        settings.skill_level_curve.push(f64::NAN);
        assert!(!is_valid_settings(&settings));

        let settings = ProjectSettings { max_energy: f64::INFINITY, ..ProjectSettings::default() };
        assert!(is_valid_settings(&settings));
    }

    #[test]
    fn weather_validation_mirrors_the_zod_refinements() {
        assert!(is_valid_weather_config(&WeatherConfig::default()));
        assert!(is_valid_weather_config(&default_weather_config()));

        let mut config = default_weather_config();
        config.types.push(WeatherTypeDefinition { crop_damage_chance: 2.0, ..WeatherTypeDefinition::default() });
        assert!(!is_valid_weather_config(&config));

        let mut config = default_weather_config();
        config.table.insert("mud".to_owned(), vec![WeatherTableEntry { weather_id: "sun".to_owned(), weight: 0.0 }]);
        assert!(!is_valid_weather_config(&config));
    }

    #[test]
    fn engine_seed_defaults_to_project_id_and_start_time() {
        let project = GameProject { id: "p1".to_owned(), game_start_time: 1.5e12, ..GameProject::default() };
        let state = create_game_state(&project, None);
        assert_eq!(state.meta.engine_seed, "p1:1500000000000");
        assert_eq!(state.rng, rng::create_rng_state("p1:1500000000000"));
        let seeded = create_game_state(&project, Some("seed-x"));
        assert_eq!(seeded.meta.engine_seed, "seed-x");
        assert_eq!(seeded.player.x, 0.5);
        assert_eq!(seeded.player.energy, 100.0);
    }
}
