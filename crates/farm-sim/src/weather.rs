//! Weather (port of `Weather.cs` / weather.ts).

use crate::engine_types::EngineContext;
use crate::rng::Rng;
use crate::schema::{GameState, WeatherTableEntry, WeatherTypeDefinition};

pub fn weather_type_by_id<'a>(ctx: &'a EngineContext, weather_id: &str) -> Option<&'a WeatherTypeDefinition> {
    let _ = (ctx, weather_id);
    todo!("port Weather.WeatherTypeById")
}

pub fn current_weather<'a>(ctx: &'a EngineContext, state: &GameState) -> Option<&'a WeatherTypeDefinition> {
    let _ = (ctx, state);
    todo!("port Weather.CurrentWeather")
}

pub fn weather_table_for_season(ctx: &EngineContext, season: &str) -> Vec<WeatherTableEntry> {
    let _ = (ctx, season);
    todo!("port Weather.WeatherTableForSeason")
}

pub fn roll_weather(ctx: &EngineContext, season: &str, rng: &mut Rng) -> String {
    let _ = (ctx, season, rng);
    todo!("port Weather.RollWeather")
}
