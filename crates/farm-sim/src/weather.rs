//! Weather (port of `Weather.cs` / weather.ts).
//!
//! Weather (M4b): rolled at each day start from a per-season weighted table; effects (rain
//! waters soil, storms damage crops, NPCs shelter) apply in the nightly pass and NPC scheduler.

use crate::engine_types::EngineContext;
use crate::rng::Rng;
use crate::schema::{GameState, WeatherTableEntry, WeatherTypeDefinition};

pub fn weather_type_by_id<'a>(ctx: &'a EngineContext, weather_id: &str) -> Option<&'a WeatherTypeDefinition> {
    ctx.content.weather.types.iter().find(|weather_type| weather_type.id == weather_id)
}

pub fn current_weather<'a>(ctx: &'a EngineContext, state: &GameState) -> Option<&'a WeatherTypeDefinition> {
    weather_type_by_id(ctx, &state.clock.weather_id)
}

/// The weighted roll table for a season. Custom seasons (M9 calendars) may not have their own
/// entry in `weather.table` — fall back to any other configured table so a creator-defined
/// season still gets weather instead of always rolling the same type.
pub fn weather_table_for_season(ctx: &EngineContext, season: &str) -> Vec<WeatherTableEntry> {
    let table = &ctx.content.weather.table;
    if let Some(own) = table.get(season) {
        if !own.is_empty() {
            return own.clone();
        }
    }
    // Object.keys order == insertion order for non-integer-like keys.
    for entries in table.values() {
        if !entries.is_empty() {
            return entries.clone();
        }
    }
    Vec::new()
}

/// Roll tomorrow's weather with the seeded RNG. Falls back to the first type.
pub fn roll_weather(ctx: &EngineContext, season: &str, rng: &mut Rng) -> String {
    let table = weather_table_for_season(ctx, season);
    let Some(first) = table.first() else {
        let types = &ctx.content.weather.types;
        return types.first().map_or_else(|| "sun".to_owned(), |weather_type| weather_type.id.clone());
    };
    let weights: Vec<f64> = table.iter().map(|entry| entry.weight).collect();
    let index = rng.weighted(&weights);
    match usize::try_from(index).ok().and_then(|i| table.get(i)) {
        Some(entry) => entry.weather_id.clone(),
        None => first.weather_id.clone(),
    }
}
