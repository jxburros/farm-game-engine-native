//! Port of `Weather.cs` (packages/engine-schemas/src/weather.ts).
//! Weather (M4b) — content-defined weather types + per-season roll tables.

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WeatherTypeDefinition {
    pub id: String,
    pub name: String,
    /// Rain-like: outdoor soil is watered automatically at day start.
    pub waters_outdoor_soil: bool,
    /// Chance (0..1) each outdoor crop is destroyed overnight (storms).
    pub crop_damage_chance: f64,
    /// NPCs with schedules stay home (skip schedule walking).
    pub npcs_stay_inside: bool,
    /// Renderer overlay hint: 'rain' | 'snow' | null (present-as-null).
    pub overlay: Option<String>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WeatherTableEntry {
    pub weather_id: String,
    /// positive.
    pub weight: f64,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WeatherConfig {
    pub types: Vec<WeatherTypeDefinition>,
    /// Per-season weighted roll tables (rolled at each day start).
    pub table: IndexMap<String, Vec<WeatherTableEntry>>,
    #[serde(flatten)]
    pub extra: Map<String, Value>,
}

fn weather_type(
    id: &str,
    name: &str,
    waters: bool,
    damage: f64,
    inside: bool,
    overlay: Option<&str>,
) -> WeatherTypeDefinition {
    WeatherTypeDefinition {
        id: id.to_owned(),
        name: name.to_owned(),
        waters_outdoor_soil: waters,
        crop_damage_chance: damage,
        npcs_stay_inside: inside,
        overlay: overlay.map(str::to_owned),
        extra: Map::new(),
    }
}

fn entry(weather_id: &str, weight: f64) -> WeatherTableEntry {
    WeatherTableEntry { weather_id: weather_id.to_owned(), weight }
}

/// The weather config projects get when they have none (TS `defaultWeatherConfig()` in
/// migrations.ts; C# `MigrationsSchema.DefaultWeatherConfig`). Lives here because content
/// assembly needs it while project migrations move to F#.
pub fn default_weather_config() -> WeatherConfig {
    let mut table = IndexMap::new();
    table.insert("spring".to_owned(), vec![entry("sun", 6.0), entry("rain", 3.0), entry("storm", 1.0)]);
    table.insert("summer".to_owned(), vec![entry("sun", 7.0), entry("rain", 1.0), entry("storm", 2.0)]);
    table.insert("fall".to_owned(), vec![entry("sun", 6.0), entry("rain", 3.0), entry("storm", 1.0)]);
    table.insert("winter".to_owned(), vec![entry("sun", 5.0), entry("snow", 5.0)]);
    WeatherConfig {
        types: vec![
            weather_type("sun", "Sunny", false, 0.0, false, None),
            weather_type("rain", "Rain", true, 0.0, false, Some("rain")),
            weather_type("storm", "Storm", true, 0.03, true, Some("rain")),
            weather_type("snow", "Snow", false, 0.0, false, Some("snow")),
        ],
        table,
        extra: Map::new(),
    }
}
