//! Game clock, calendar and the overnight pass (port of `GameTime.cs` / time.ts).
//!
//! Game clock & calendar (M2, made creator-configurable in M9). Ticks advance in-game minutes;
//! the `sleep` command (or a collapse) ends the day; the last day of a season rolls to the next
//! season; wrapping past the last season rolls the year. The nightly pass is where the world
//! "lives": crops grow, soil dries, nodes respawn, energy restores.
//!
//! All calendar math below is driven by a project's `settings.calendar` (seasons + their
//! lengths, plus festival days) rather than a hardcoded 4×28 layout. Every helper takes the
//! resolved [`CalendarConfig`] — usually `ctx.content.settings.calendar` — rather than a full
//! [`EngineContext`], so calendar math stays a pure, easily-testable function of calendar + day.

use crate::animals;
use crate::crafting;
use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::farming::crops;
use crate::hooks::{DayHookPayload, HookEvent, SeasonChangeHookPayload, WeatherRollHookPayload, YearStartHookPayload};
use crate::rng::Rng;
use crate::schema::{
    classic_calendar_seasons, soil_states, tile_types, CalendarConfig, CalendarFestival, CalendarSeason, GameState,
    NodeTypeDefinition, TileNode,
};
use crate::units;
use crate::weather;
use indexmap::IndexMap;
use std::sync::LazyLock;

/// Day phases (TS `DayPhase` union).
pub mod day_phases {
    pub const MORNING: &str = "morning";
    pub const DAY: &str = "day";
    pub const EVENING: &str = "evening";
    pub const NIGHT: &str = "night";
}

/// TS `SleepOptions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SleepOptions {
    pub collapsed: bool,
}

/// The classic four-season fallback, built once so lookups can hand out references to it with
/// the calendar's lifetime.
static CLASSIC_SEASONS: LazyLock<Vec<CalendarSeason>> = LazyLock::new(classic_calendar_seasons);

/// [`calendar_seasons`] as borrowed entries: the project's positive-length seasons, or the
/// classic fallback.
fn effective_seasons(calendar: &CalendarConfig) -> Vec<&CalendarSeason> {
    let seasons: Vec<&CalendarSeason> = calendar.seasons.iter().filter(|season| season.days > 0).collect();
    if seasons.is_empty() {
        CLASSIC_SEASONS.iter().collect()
    } else {
        seasons
    }
}

/// Effective season list: the project's calendar, or the classic four-season/28-day fallback
/// when it's empty or every season has a non-positive length.
pub fn calendar_seasons(calendar: &CalendarConfig) -> Vec<CalendarSeason> {
    effective_seasons(calendar).into_iter().cloned().collect()
}

fn calendar_year_length(seasons: &[&CalendarSeason]) -> i64 {
    seasons.iter().map(|season| i64::from(season.days)).sum()
}

/// Look up a calendar season by id (searches the effective, fallback-safe list).
pub fn season_by_id<'a>(calendar: &'a CalendarConfig, id: &str) -> Option<&'a CalendarSeason> {
    effective_seasons(calendar).into_iter().find(|season| season.id == id)
}

/// TS `CalendarPosition` (the private C# record).
struct CalendarPosition<'a> {
    season: &'a CalendarSeason,
    /// Carried by the TS/C# shape; nothing reads it.
    #[allow(dead_code)]
    season_index: usize,
    /// 1-based day within `season`.
    day_of_season: u32,
}

/// Resolve an absolute (1-based) day to its season + day-of-season, honoring each season's own
/// length via cumulative offsets.
fn position_for_day(calendar: &CalendarConfig, absolute_day: u32) -> CalendarPosition<'_> {
    let seasons = effective_seasons(calendar);
    let year_length = calendar_year_length(&seasons);
    let day_in_year = (i64::from(absolute_day) - 1).rem_euclid(year_length);
    let mut remaining = day_in_year;
    for (i, season) in seasons.iter().enumerate() {
        let days = i64::from(season.days);
        if remaining < days {
            // remaining < days ≤ u32::MAX
            return CalendarPosition { season, season_index: i, day_of_season: remaining as u32 + 1 };
        }
        remaining -= days;
    }
    // Unreachable when yearLength = sum(seasons.days), kept for type safety. The effective
    // list is never empty (the classic fallback has four seasons).
    let last_index = seasons.len() - 1;
    let last = seasons[last_index];
    CalendarPosition { season: last, season_index: last_index, day_of_season: last.days }
}

/// 1-based day within the current season for an absolute (1-based) day.
pub fn day_of_season(calendar: &CalendarConfig, absolute_day: u32) -> u32 {
    position_for_day(calendar, absolute_day).day_of_season
}

pub fn season_for_day(calendar: &CalendarConfig, absolute_day: u32) -> String {
    position_for_day(calendar, absolute_day).season.id.clone()
}

pub fn year_for_day(calendar: &CalendarConfig, absolute_day: u32) -> u32 {
    let year_length = calendar_year_length(&effective_seasons(calendar));
    // Day 0 is in year 0.
    u32::try_from((i64::from(absolute_day) - 1).div_euclid(year_length) + 1).unwrap_or(0)
}

/// The festival configured for the given absolute day, if any.
pub fn festival_on_day(calendar: &CalendarConfig, absolute_day: u32) -> Option<&CalendarFestival> {
    if calendar.festivals.is_empty() {
        return None;
    }
    let position = position_for_day(calendar, absolute_day);
    calendar
        .festivals
        .iter()
        .find(|festival| festival.season_id == position.season.id && festival.day == position.day_of_season)
}

/// Format a time of day (micro-minutes) as a clock string, e.g. 810 minutes → "1:30 PM".
pub fn format_time_of_day(time_minutes: u32) -> String {
    let total = units::whole_minute(time_minutes) % units::MINUTES_PER_DAY;
    let hours24 = total / 60;
    let minutes = total % 60;
    let suffix = if hours24 < 12 { "AM" } else { "PM" };
    let hours12 = if hours24.is_multiple_of(12) { 12 } else { hours24 % 12 };
    format!("{hours12}:{minutes:0>2} {suffix}")
}

/// Returns one of [`day_phases`] for a time of day (micro-minutes).
pub fn day_phase(time_minutes: u32) -> &'static str {
    let t = u64::from(time_minutes) % (u64::from(units::MINUTES_PER_DAY) * u64::from(units::MINUTE));
    let hour = |h: u64| h * 60 * u64::from(units::MINUTE);
    if (hour(5)..hour(10)).contains(&t) {
        return day_phases::MORNING;
    }
    if (hour(10)..hour(17)).contains(&t) {
        return day_phases::DAY;
    }
    if (hour(17)..hour(21)).contains(&t) {
        return day_phases::EVENING;
    }
    day_phases::NIGHT
}

/// The overnight pass: advances the clock, weather, crops, animals, machines, NPC schedules.
///
/// End the day: nightly world pass + calendar roll + energy restore.
/// Emits onDayEnd / onSeasonChange / onYearStart / onDayStart hooks.
pub fn perform_sleep(ctx: &EngineContext, state: &mut GameState, options: SleepOptions) -> Effects {
    let mut effects: Effects = Vec::new();
    let settings = &ctx.content.settings;
    let previous_day = state.clock.day;
    let previous_season = state.clock.season.clone();
    let previous_year = state.clock.year;
    let previous_weather_id = state.clock.weather_id.clone();

    ctx.emit(HookEvent::DayEnd(DayHookPayload {
        day: previous_day,
        season: previous_season.clone(),
        year: previous_year,
    }));

    let new_day = previous_day.saturating_add(1);
    let calendar = &settings.calendar;
    let seasons = calendar_seasons(calendar);
    // Season progression is relative to the CURRENT season so authored projects may start in
    // any season regardless of the absolute day. dayOfSeason(newDay) === 1 detects a season
    // boundary at the calendar's cumulative offsets — correct for uneven season lengths too,
    // since only one day elapses per sleep.
    let season_rolls = day_of_season(calendar, new_day) == 1;
    // Math.Max(0, FindIndex(…)): a missing season counts as the first one.
    let previous_season_index = seasons.iter().position(|season| season.id == previous_season).unwrap_or(0);
    let new_season = if season_rolls {
        seasons[(previous_season_index + 1) % seasons.len()].id.clone()
    } else {
        previous_season.clone()
    };
    let new_year =
        if season_rolls && new_season == seasons[0].id { previous_year.saturating_add(1) } else { previous_year };

    // Object.fromEntries: later duplicates win.
    let mut node_types: IndexMap<&str, &NodeTypeDefinition> = IndexMap::new();
    for def in &ctx.content.node_types {
        node_types.insert(def.id.as_str(), def);
    }

    // Roll the new day's weather with the seeded RNG (M4). Its effects (rain watering, storm
    // damage) apply while the world pass runs below.
    let mut rng = Rng::new(state.rng.clone());
    let rolled_weather_id = if ctx.content.weather.types.is_empty() {
        state.clock.weather_id.clone()
    } else {
        weather::roll_weather(ctx, &new_season, &mut rng)
    };
    // onWeatherRoll has reroll capability: a listener may return { weatherId } to override the
    // roll (last valid override wins; order is deterministic subscription order). The Rust bus
    // hands back the `weatherId` strings themselves (the host unwraps each listener's answer),
    // so `ReadWeatherIdOverride` reduces to the known-type check.
    let roll_responses =
        ctx.collect_weather_roll(WeatherRollHookPayload { weather_id: rolled_weather_id.clone(), day: new_day });
    let mut new_weather_id = rolled_weather_id;
    for response in &roll_responses {
        if let Some(weather_override) = read_weather_id_override(response) {
            if ctx.content.weather.types.iter().any(|weather_type| weather_type.id == weather_override) {
                new_weather_id = weather_override.to_owned();
            }
        }
    }
    let weather_def = ctx.content.weather.types.iter().find(|weather_type| weather_type.id == new_weather_id);
    let crop_damage_chance = weather_def.map_or(0, |def| def.crop_damage_chance);
    let waters_outdoor_soil = weather_def.is_some_and(|def| def.waters_outdoor_soil);

    // Nightly world pass (C# cloned every tile grid; the reducer edits the tiles in place)
    for scene in &mut state.world.scenes {
        for row in &mut scene.tiles {
            for tile in row.iter_mut() {
                // Storm damage rolls before growth (the storm hits overnight). The draw only
                // happens for a live crop under damaging weather (short-circuit order).
                if tile.crop.as_ref().is_some_and(|crop| crop.withered != Some(true))
                    && crop_damage_chance > 0
                    && rng.chance(crop_damage_chance)
                {
                    tile.crop = None;
                }
                if let Some(crop) = tile.crop.as_mut() {
                    let definition = ctx.content.crops.get(&crop.r#type);
                    if crop.withered != Some(true) {
                        if let Some(definition) = definition {
                            if crop.watered {
                                crop.days_grown = Some(crop.days_grown.unwrap_or(0).saturating_add(1));
                                crop.days_without_water = 0;
                            } else {
                                crop.days_without_water = crop.days_without_water.saturating_add(1);
                            }
                            crop.watered = false;
                            crop.stage = crops::compute_crop_stage(crop, definition);
                            // Season check happens against the NEW season.
                            if !definition.seasons.contains(&new_season) {
                                crop.withered = Some(true);
                            }
                        }
                    }
                }

                // Unfertilized watered soil dries out overnight — unless the new day's weather
                // waters it (rain/storm, M4).
                if waters_outdoor_soil && tile.background == tile_types::SOIL {
                    tile.soil_moisture = 100;
                    tile.soil_state = Some(
                        if tile.soil_state.as_deref() == Some(soil_states::FERTILIZED) {
                            soil_states::FERTILIZED
                        } else {
                            soil_states::WATERED
                        }
                        .to_owned(),
                    );
                    if let Some(crop) = tile.crop.as_mut() {
                        if crop.withered != Some(true) {
                            crop.watered = true;
                            crop.last_watered_day = Some(new_day);
                            crop.days_without_water = 0;
                        }
                    }
                } else {
                    tile.soil_moisture = 0;
                    if tile.soil_state.as_deref() == Some(soil_states::WATERED) {
                        tile.soil_state = Some(soil_states::DRY.to_owned());
                    }
                }

                // Node respawns
                let respawned = tile.node.as_ref().and_then(|node| {
                    let depleted_on_day = node.depleted_on_day?;
                    let definition = node_types.get(node.type_id.as_str())?;
                    let respawn_days = definition.respawn_after()?;
                    (i64::from(new_day) - i64::from(depleted_on_day) >= i64::from(respawn_days)).then(|| TileNode {
                        type_id: node.type_id.clone(),
                        remaining_health: definition.health,
                        ..TileNode::default()
                    })
                });
                if let Some(node) = respawned {
                    tile.node = Some(node);
                }
            }
        }
    }

    // Energy restore / collapse penalty
    let max_energy = state.player.max_energy;
    let mut money = state.player.money;
    if options.collapsed {
        let penalty = money.min(settings.collapse_money_penalty);
        money = money.saturating_sub(penalty);
        effects
            .push(Effect::message(message_levels::ERROR, format!("You collapsed from exhaustion! Lost ${penalty}.")));
    }
    // A collapse restores round(max × fraction) whole points (the fraction in thousandths).
    let energy = if options.collapsed {
        let whole_points = units::div_round(
            i64::from(max_energy) * i64::from(settings.collapse_energy_fraction),
            i64::from(units::MILLI_ONE) * i64::from(units::ENERGY_POINT),
        );
        units::points(i32::try_from(whole_points).unwrap_or(i32::MAX))
    } else {
        max_energy
    };

    state.clock.day = new_day;
    state.clock.season = new_season.clone();
    state.clock.year = new_year;
    state.clock.time_minutes = units::minutes(settings.time.day_start_minute);
    state.clock.weather_id = new_weather_id.clone();
    state.player.energy = energy;
    state.player.money = money;
    state.rng = rng.state;
    state.dialogue = None;
    state.shop = None;
    state.shop_purchases_today = IndexMap::new();

    // Animals: age, mood, product rolls (M4c). Machines finish overnight jobs automatically
    // since completion is an absolute-minute comparison (M4a).
    animals::advance_animals_nightly(ctx, state);
    crafting::settle_machines(ctx, state);

    if let Some(weather_def) = weather_def {
        if new_weather_id != previous_weather_id {
            effects.push(Effect::message(message_levels::INFO, format!("Weather: {}", weather_def.name)));
        }
    }

    if new_season != previous_season {
        let new_season_name = seasons
            .iter()
            .find(|season| season.id == new_season)
            .map_or_else(|| new_season.clone(), |s| s.name.clone());
        effects.push(Effect::message(message_levels::INFO, format!("{new_season_name} has arrived!")));
        ctx.emit(HookEvent::SeasonChange(SeasonChangeHookPayload {
            season: new_season.clone(),
            previous_season: previous_season.clone(),
            year: new_year,
        }));
    }
    if new_year != previous_year {
        effects.push(Effect::message(message_levels::INFO, format!("Year {new_year} begins!")));
        ctx.emit(HookEvent::YearStart(YearStartHookPayload { year: new_year }));
    }

    effects.push(Effect::DayStarted { day: new_day, season: new_season.clone(), year: new_year });
    effects.push(Effect::message(
        message_levels::SUCCESS,
        format!("Day {} of {new_season}, Year {new_year}", day_of_season(calendar, new_day)),
    ));

    if let Some(festival) = festival_on_day(calendar, new_day) {
        effects.push(Effect::message(message_levels::INFO, format!("Today is the {}!", festival.name)));
    }

    ctx.emit(HookEvent::DayStart(DayHookPayload { day: new_day, season: new_season, year: new_year }));

    effects
}

/// TS `(response as { weatherId?: unknown } | null)?.weatherId`, then `typeof override === 'string'`.
/// The Rust [`crate::hooks::WeatherRollListener`] already answers with the `weatherId` strings
/// (one per responding listener), so every response is a string override.
fn read_weather_id_override(response: &str) -> Option<&str> {
    Some(response)
}
