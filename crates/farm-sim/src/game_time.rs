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
use crate::farming::{crops, multi_tile};
use crate::hooks::{DayHookPayload, HookEvent, SeasonChangeHookPayload, WeatherRollHookPayload, YearStartHookPayload};
use crate::messages;
use crate::quests;
use crate::rng::Rng;
use crate::schema::{
    classic_calendar_seasons, soil_states, tile_types, CalendarConfig, CalendarFestival, CalendarSeason, ClockState,
    Crop, GameState, NodeTypeDefinition, Tile, TileNode, TimeConfig,
};
use crate::units;
use crate::weather;
use indexmap::IndexMap;
use std::sync::LazyLock;

/// TS `SleepOptions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SleepOptions {
    pub collapsed: bool,
}

/// The classic four-season fallback, built once so lookups can hand out references to it with
/// the calendar's lifetime.
static CLASSIC_SEASONS: LazyLock<Vec<CalendarSeason>> = LazyLock::new(classic_calendar_seasons);

/// [`calendar_seasons`] as borrowed entries: the project's positive-length seasons, or the
/// classic fallback. A season id the calendar repeats counts once, at its first place (Problems
/// reports it as `calendar.duplicateSeason`): stepping by id could otherwise never reach the
/// seasons after the repeat.
fn effective_seasons(calendar: &CalendarConfig) -> Vec<&CalendarSeason> {
    let mut seasons: Vec<&CalendarSeason> = Vec::with_capacity(calendar.seasons.len());
    for season in &calendar.seasons {
        if season.days > 0 && !seasons.iter().any(|seen| seen.id == season.id) {
            seasons.push(season);
        }
    }
    if seasons.is_empty() {
        CLASSIC_SEASONS.iter().collect()
    } else {
        seasons
    }
}

/// Effective season list: the project's calendar, or the classic four-season/28-day fallback
/// when it's empty or every season has a non-positive length (a repeated id counts once).
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

/// A place in the calendar: a season and a day within it (TS `CalendarPosition`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CalendarDate<'a> {
    pub season: &'a CalendarSeason,
    /// Index of `season` in the effective season list ([`calendar_seasons`]).
    pub season_index: usize,
    /// 1-based day within `season`.
    pub day_of_season: u32,
}

/// Resolve an absolute (1-based) day to its season + day-of-season, honoring each season's own
/// length via cumulative offsets. This is the calendar of a game that started on day 1 of the
/// first season; the running clock carries its own place ([`clock_date`]).
fn position_for_day(calendar: &CalendarConfig, absolute_day: u32) -> CalendarDate<'_> {
    let seasons = effective_seasons(calendar);
    let year_length = calendar_year_length(&seasons);
    let day_in_year = (i64::from(absolute_day) - 1).rem_euclid(year_length);
    let mut remaining = day_in_year;
    for (i, season) in seasons.iter().enumerate() {
        let days = i64::from(season.days);
        if remaining < days {
            // remaining < days ≤ u32::MAX
            return CalendarDate { season, season_index: i, day_of_season: remaining as u32 + 1 };
        }
        remaining -= days;
    }
    // Unreachable when yearLength = sum(seasons.days), kept for type safety. The effective
    // list is never empty (the classic fallback has four seasons).
    let last_index = seasons.len() - 1;
    let last = seasons[last_index];
    CalendarDate { season: last, season_index: last_index, day_of_season: last.days }
}

/// Where an absolute (1-based) day falls in a game that started on day 1 of the first season.
pub fn natural_date(calendar: &CalendarConfig, absolute_day: u32) -> CalendarDate<'_> {
    position_for_day(calendar, absolute_day)
}

/// The clock's place in the calendar: `clock.season` and `clock.day_of_season` (#23). A season
/// the calendar lacks counts as the first one (Problems warns about it); a day past the end of
/// a season that got shorter is its last day; a clock without a day of season (a save written
/// before the field existed) takes the absolute day's place in its season.
pub fn clock_date<'a>(calendar: &'a CalendarConfig, clock: &ClockState) -> CalendarDate<'a> {
    let seasons = effective_seasons(calendar);
    let season_index = seasons.iter().position(|season| season.id == clock.season).unwrap_or(0);
    let season = seasons[season_index];
    let day = if clock.day_of_season == 0 {
        position_for_day(calendar, clock.day).day_of_season
    } else {
        clock.day_of_season
    };
    CalendarDate { season, season_index, day_of_season: day.clamp(1, season.days) }
}

/// Stores [`clock_date`]'s day of season in the clock, so a clock read from a project or an
/// older save, or kept across a calendar change, names a day its season has. The season id
/// stays as it is.
pub fn reconcile_clock(calendar: &CalendarConfig, clock: &mut ClockState) {
    clock.day_of_season = clock_date(calendar, clock).day_of_season;
}

/// 1-based day within the current season for an absolute (1-based) day.
pub fn day_of_season(calendar: &CalendarConfig, absolute_day: u32) -> u32 {
    position_for_day(calendar, absolute_day).day_of_season
}

/// The season id of an absolute day, in a calendar that started on day 1 of the first season.
pub fn season_for_day(calendar: &CalendarConfig, absolute_day: u32) -> String {
    position_for_day(calendar, absolute_day).season.id.clone()
}

/// The festival configured for the given absolute day, if any, in the calendar of a game that
/// started on day 1 of the first season (the running game uses [`festival_today`]).
pub fn festival_on_day(calendar: &CalendarConfig, absolute_day: u32) -> Option<&CalendarFestival> {
    let position = position_for_day(calendar, absolute_day);
    festival_on(calendar, &position.season.id, position.day_of_season)
}

/// The festival on `day_of_season` of `season_id`, if any.
pub fn festival_on<'a>(
    calendar: &'a CalendarConfig,
    season_id: &str,
    day_of_season: u32,
) -> Option<&'a CalendarFestival> {
    calendar.festivals.iter().find(|festival| festival.season_id == season_id && festival.day == day_of_season)
}

/// Today's festival, if any.
pub fn festival_today<'a>(calendar: &'a CalendarConfig, clock: &ClockState) -> Option<&'a CalendarFestival> {
    let date = clock_date(calendar, clock);
    festival_on(calendar, &date.season.id, date.day_of_season)
}

/// The shortest day a project may configure: `dayEndMinute - dayStartMinute` (#24).
pub const MIN_DAY_WINDOW_MINUTES: u32 = 60;
/// The latest `dayEndMinute` the clock can reach: the clock counts micro-minutes in a `u32`,
/// so a later end would never come and the day would never end (#24). 4294 = 71:34.
pub const MAX_DAY_END_MINUTE: u32 = u32::MAX / units::MINUTE;
/// The fastest clock a project may configure, in in-game minutes per real second (a whole day
/// per second).
pub const MAX_MINUTES_PER_REAL_SECOND: u32 = units::MINUTES_PER_DAY;

static DEFAULT_TIME: LazyLock<TimeConfig> = LazyLock::new(TimeConfig::default);

/// Does `time` describe a day the clock can run through: it starts before it ends, lasts at
/// least [`MIN_DAY_WINDOW_MINUTES`], ends by [`MAX_DAY_END_MINUTE`], and the clock moves but at
/// most [`MAX_MINUTES_PER_REAL_SECOND`]? A start at or after the end collapsed the player on
/// every tick (#24).
pub fn is_valid_time_config(time: &TimeConfig) -> bool {
    let max_rate =
        u64::from(MAX_MINUTES_PER_REAL_SECOND) * u64::from(units::MINUTE) / u64::from(units::TICKS_PER_SECOND);
    time.day_start_minute < time.day_end_minute
        && time.day_end_minute - time.day_start_minute >= MIN_DAY_WINDOW_MINUTES
        && time.day_end_minute <= MAX_DAY_END_MINUTE
        && time.minutes_per_real_second > 0
        && u64::from(time.minutes_per_real_second) <= max_rate
}

/// The time settings the engine runs: `time`, or the defaults when [`is_valid_time_config`]
/// rejects it. Settings resolution already replaces an invalid time section; this guards
/// content that skipped it (a hand-edited cartridge or content JSON).
pub fn time_config(time: &TimeConfig) -> &TimeConfig {
    if is_valid_time_config(time) {
        time
    } else {
        &DEFAULT_TIME
    }
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

    let next = next_date(&settings.calendar, &state.clock);

    // Roll the new day's weather with the seeded RNG (M4). Its effects (rain watering, storm
    // damage) apply while the world pass runs below.
    let mut rng = Rng::new(state.rng.clone());
    let new_weather_id = roll_next_weather(ctx, state, &next, &mut rng);
    let weather_def = ctx.content.weather.types.iter().find(|weather_type| weather_type.id == new_weather_id);
    let night = NightWeather {
        crop_damage_chance: weather_def.map_or(0, |def| def.crop_damage_chance),
        waters_outdoor_soil: weather_def.is_some_and(|def| def.waters_outdoor_soil),
    };

    // Multi-tile crops grow and fall as one (watered together; a storm takes the whole crop).
    let multi_tile_crops = multi_tile::before_night(state);
    night_world_pass(ctx, state, &next, night, &mut rng);
    multi_tile::after_night(state, &multi_tile_crops);

    let (energy, money) = wake_energy_and_money(ctx, state, options, &mut effects);

    state.clock.day = next.day;
    state.clock.season = next.season.clone();
    state.clock.day_of_season = next.day_of_season;
    state.clock.year = next.year;
    state.clock.time_minutes = units::minutes(time_config(&settings.time).day_start_minute);
    state.clock.weather_id = new_weather_id.clone();
    state.player.energy = energy;
    state.player.money = money;
    state.rng = rng.state;
    state.dialogue = None;
    state.shop = None;
    state.minigame = None;
    state.shop_purchases_today = IndexMap::new();

    // Animals: age, mood, product rolls (M4c). Machines finish overnight jobs automatically
    // since completion is an absolute-minute comparison (M4a).
    animals::advance_animals_nightly(ctx, state);
    crafting::settle_machines(ctx, state);
    effects.extend(quests::restart_repeatable_quests(ctx, state));

    if let Some(weather_def) = weather_def {
        if new_weather_id != previous_weather_id {
            effects.push(Effect::say(message_levels::INFO, messages::WEATHER_TODAY.with(&[&weather_def.name])));
        }
    }
    announce_new_day(ctx, &next, &previous_season, previous_year, &mut effects);
    effects
}

/// The date the night rolls the clock to.
struct NextDate {
    day: u32,
    season: String,
    /// Index of `season` in the effective season list.
    season_index: usize,
    day_of_season: u32,
    year: u32,
}

/// Tomorrow. The calendar steps from the clock's own season and day of season (#23), so a game
/// may start in any season and every season lasts its configured length; the absolute day only
/// counts days.
fn next_date(calendar: &CalendarConfig, clock: &ClockState) -> NextDate {
    let seasons = effective_seasons(calendar);
    let today = clock_date(calendar, clock);
    let season_rolls = today.day_of_season >= today.season.days;
    let (season_index, day_of_season) = if season_rolls {
        ((today.season_index + 1) % seasons.len(), 1)
    } else {
        (today.season_index, today.day_of_season + 1)
    };
    let season = if season_rolls { seasons[season_index].id.clone() } else { clock.season.clone() };
    let year = if season_rolls && season_index == 0 { clock.year.saturating_add(1) } else { clock.year };
    NextDate { day: clock.day.saturating_add(1), season, season_index, day_of_season, year }
}

/// The new day's weather: the seeded roll for the new season (the current weather when the
/// game has no weather types), then the `onWeatherRoll` overrides.
fn roll_next_weather(ctx: &EngineContext, state: &GameState, next: &NextDate, rng: &mut Rng) -> String {
    let rolled_weather_id = if ctx.content.weather.types.is_empty() {
        state.clock.weather_id.clone()
    } else {
        weather::roll_weather(ctx, &next.season, rng)
    };
    // onWeatherRoll has reroll capability: a listener may return { weatherId } to override the
    // roll (last valid override wins; order is deterministic subscription order). The Rust bus
    // hands back the `weatherId` strings themselves (the host unwraps each listener's answer),
    // so `ReadWeatherIdOverride` reduces to the known-type check.
    let roll_responses =
        ctx.collect_weather_roll(WeatherRollHookPayload { weather_id: rolled_weather_id.clone(), day: next.day });
    let mut new_weather_id = rolled_weather_id;
    for response in &roll_responses {
        if let Some(weather_override) = read_weather_id_override(response) {
            if ctx.content.weather.types.iter().any(|weather_type| weather_type.id == weather_override) {
                new_weather_id = weather_override.to_owned();
            }
        }
    }
    new_weather_id
}

/// What the new day's weather does to outdoor tiles overnight.
#[derive(Clone, Copy)]
struct NightWeather {
    /// Chance that a storm destroys a live crop (a [`units::Probability`] threshold).
    crop_damage_chance: u64,
    /// Rain or a storm waters the soil.
    waters_outdoor_soil: bool,
}

/// The nightly world pass over every tile of every scene: storm damage, crop growth, soil
/// drying or rain, node respawns (the C# cloned every tile grid; the reducer edits the tiles in
/// place).
fn night_world_pass(ctx: &EngineContext, state: &mut GameState, next: &NextDate, night: NightWeather, rng: &mut Rng) {
    // Object.fromEntries: later duplicates win.
    let mut node_types: IndexMap<&str, &NodeTypeDefinition> = IndexMap::new();
    for def in &ctx.content.node_types {
        node_types.insert(def.id.as_str(), def);
    }

    for scene in &mut state.world.scenes {
        // Weather stays outside indoor scenes (greenhouses, interiors, mine floors).
        let outdoor = !scene.is_indoor();
        for row in &mut scene.tiles {
            for tile in row.iter_mut() {
                // Storm damage rolls before growth (the storm hits overnight). The draw only
                // happens for a live crop under damaging weather (short-circuit order).
                if outdoor
                    && tile.crop.as_ref().is_some_and(|crop| crop.withered != Some(true))
                    && night.crop_damage_chance > 0
                    && rng.chance(night.crop_damage_chance)
                {
                    tile.crop = None;
                }
                if let Some(crop) = tile.crop.as_mut() {
                    grow_crop_overnight(ctx, crop, &next.season);
                }
                settle_soil_overnight(tile, outdoor && night.waters_outdoor_soil, next.day);
                respawn_node(tile, &node_types, next.day);
            }
        }
    }
}

/// One night of growth for a live crop: a watered crop grows a day, a dry one goes a day
/// without water, and a crop out of the new season withers. A withered crop or one of an
/// unknown type is left alone.
fn grow_crop_overnight(ctx: &EngineContext, crop: &mut Crop, new_season: &str) {
    if crop.withered == Some(true) {
        return;
    }
    let Some(definition) = ctx.content.crops.get(&crop.r#type) else { return };
    if crop.watered {
        crop.days_grown = Some(crop.days_grown.unwrap_or(0).saturating_add(1));
        crop.days_without_water = 0;
    } else {
        crop.days_without_water = crop.days_without_water.saturating_add(1);
    }
    crop.watered = false;
    crop.stage = crops::compute_crop_stage(crop, definition);
    // Season check happens against the NEW season.
    if !definition.seasons.iter().any(|season| season == new_season) {
        crop.withered = Some(true);
    }
}

/// Unfertilized watered soil dries out overnight — unless the new day's weather waters it
/// (`rained`: rain or a storm on an outdoor tile, M4), which also waters a live crop.
fn settle_soil_overnight(tile: &mut Tile, rained: bool, new_day: u32) {
    if rained && tile.background == tile_types::SOIL {
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
}

/// A depleted node whose type respawns comes back at full health once enough days passed.
fn respawn_node(tile: &mut Tile, node_types: &IndexMap<&str, &NodeTypeDefinition>, new_day: u32) {
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

/// The player's energy and money in the morning: full energy after a sleep; after a collapse
/// the money penalty (with its message) and part of the energy.
fn wake_energy_and_money(
    ctx: &EngineContext,
    state: &GameState,
    options: SleepOptions,
    effects: &mut Effects,
) -> (i32, i64) {
    let settings = &ctx.content.settings;
    let max_energy = state.player.max_energy;
    let mut money = state.player.money;
    if !options.collapsed {
        return (max_energy, money);
    }
    let penalty = money.min(settings.collapse_money_penalty);
    money = money.saturating_sub(penalty);
    effects.push(Effect::say(message_levels::ERROR, messages::COLLAPSED.with(&[&penalty])));
    // A collapse restores round(max × fraction) whole points (the fraction in thousandths).
    let whole_points = units::div_round(
        i64::from(max_energy) * i64::from(settings.collapse_energy_fraction),
        i64::from(units::MILLI_ONE) * i64::from(units::ENERGY_POINT),
    );
    (units::points(i32::try_from(whole_points).unwrap_or(i32::MAX)), money)
}

/// The morning's messages and hooks: a new season or year, the new day, today's festival.
fn announce_new_day(
    ctx: &EngineContext,
    next: &NextDate,
    previous_season: &str,
    previous_year: u32,
    effects: &mut Effects,
) {
    let calendar = &ctx.content.settings.calendar;
    if next.season != previous_season {
        let new_season_name = effective_seasons(calendar)[next.season_index].name.clone();
        effects.push(Effect::say(message_levels::INFO, messages::SEASON_ARRIVED.with(&[&new_season_name])));
        ctx.emit(HookEvent::SeasonChange(SeasonChangeHookPayload {
            season: next.season.clone(),
            previous_season: previous_season.to_owned(),
            year: next.year,
        }));
    }
    if next.year != previous_year {
        effects.push(Effect::say(message_levels::INFO, messages::YEAR_BEGINS.with(&[&next.year])));
        ctx.emit(HookEvent::YearStart(YearStartHookPayload { year: next.year }));
    }

    effects.push(Effect::DayStarted { day: next.day, season: next.season.clone(), year: next.year });
    effects.push(Effect::say(
        message_levels::SUCCESS,
        messages::DAY_OF_SEASON.with_args(vec![
            messages::Arg::text(next.day_of_season),
            messages::season_noun(&next.season),
            messages::Arg::text(next.year),
        ]),
    ));

    if let Some(festival) = festival_on(calendar, &next.season, next.day_of_season) {
        effects.push(Effect::say(message_levels::INFO, messages::FESTIVAL_TODAY.with(&[&festival.name])));
    }

    ctx.emit(HookEvent::DayStart(DayHookPayload { day: next.day, season: next.season.clone(), year: next.year }));
}

/// TS `(response as { weatherId?: unknown } | null)?.weatherId`, then `typeof override === 'string'`.
/// The Rust [`crate::hooks::WeatherRollListener`] already answers with the `weatherId` strings
/// (one per responding listener), so every response is a string override.
fn read_weather_id_override(response: &str) -> Option<&str> {
    Some(response)
}
