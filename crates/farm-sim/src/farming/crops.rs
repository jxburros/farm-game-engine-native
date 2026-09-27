//! Farming math — extracted from src/lib/crops.ts, behavior-identical (characterization-tested),
//! with all randomness going through the seeded RNG instead of Math.random. (Port of
//! `Farming/Crops.cs`, engine-core/src/farming/crops.ts.)

use crate::content_builtin;
use crate::schema::{CropDefinition, CustomCropDefinition};
use indexmap::IndexMap;
use serde_json::{Map, Value};

/// Merge built-in crop definitions with a project's custom crops.
pub fn merge_crop_definitions(custom_crops: &[CropDefinition]) -> IndexMap<String, CropDefinition> {
    let mut merged = content_builtin::crop_definitions();
    for crop in custom_crops {
        merged.insert(crop.id.clone(), crop.clone());
    }
    merged
}

/// `project.customCrops` variant: in TS a CustomCropDefinition IS a CropDefinition (structural
/// typing, passthrough keeps `customAsset`); here each one is converted via
/// [`to_crop_definition`].
pub fn merge_custom_crop_definitions(custom_crops: &[CustomCropDefinition]) -> IndexMap<String, CropDefinition> {
    let converted: Vec<CropDefinition> = custom_crops.iter().map(to_crop_definition).collect();
    merge_crop_definitions(&converted)
}

/// CustomCropDefinition → CropDefinition: every key survives, `customAsset` (and any
/// passthrough extras) ride along in `extra` exactly like the TS object would carry them.
pub fn to_crop_definition(custom: &CustomCropDefinition) -> CropDefinition {
    let mut extra = Map::new();
    if let Some(asset) = &custom.custom_asset {
        extra.insert("customAsset".to_owned(), Value::String(asset.clone()));
    }
    for (key, value) in &custom.extra {
        extra.insert(key.clone(), value.clone());
    }
    CropDefinition {
        id: custom.id.clone(),
        name: custom.name.clone(),
        visual: custom.visual.clone(),
        seed_cost: custom.seed_cost,
        base_harvest_value: custom.base_harvest_value,
        growth_time: custom.growth_time,
        growth_days: custom.growth_days,
        stages: custom.stages,
        seasons: custom.seasons.clone(),
        regrowth_time: custom.regrowth_time,
        regrowth_days: custom.regrowth_days,
        can_regrow: custom.can_regrow,
        multi_tile: custom.multi_tile.clone(),
        mutation_chance: custom.mutation_chance,
        yield_min: custom.yield_min,
        yield_max: custom.yield_max,
        extra,
    }
}

/// CropDefinition → CustomCropDefinition (a pack crop stored as a project custom crop): a string
/// `customAsset` in the extras becomes the typed field, like the C# JSON round-trip.
pub fn to_custom_crop_definition(crop: &CropDefinition) -> CustomCropDefinition {
    let mut extra = crop.extra.clone();
    let custom_asset = match extra.get("customAsset") {
        Some(Value::String(asset)) => {
            let asset = asset.clone();
            extra.remove("customAsset");
            Some(asset)
        }
        _ => None,
    };
    CustomCropDefinition {
        id: crop.id.clone(),
        name: crop.name.clone(),
        visual: crop.visual.clone(),
        seed_cost: crop.seed_cost,
        base_harvest_value: crop.base_harvest_value,
        growth_time: crop.growth_time,
        growth_days: crop.growth_days,
        stages: crop.stages,
        seasons: crop.seasons.clone(),
        regrowth_time: crop.regrowth_time,
        regrowth_days: crop.regrowth_days,
        can_regrow: crop.can_regrow,
        multi_tile: crop.multi_tile.clone(),
        mutation_chance: crop.mutation_chance,
        yield_min: crop.yield_min,
        yield_max: crop.yield_max,
        custom_asset,
        extra,
    }
}

// ─── Growth and harvest math (port of the rest of Farming/Crops.cs) ─────────────────────────

use crate::js;
use crate::rng::RandomSource;
use crate::schema::{crop_mutations, crop_qualities, tile_types, Crop, GameContent, Tile};

pub fn get_crop_definition_from_content<'a>(content: &'a GameContent, crop_type: &str) -> Option<&'a CropDefinition> {
    content.crops.get(crop_type)
}

pub fn get_crop_stage(planted_at: f64, current_time: f64, growth_time: f64, stages: f64, watered: bool) -> f64 {
    let elapsed = current_time - planted_at;
    let stage_time = growth_time / stages;
    let water_penalty = if watered { 1.0 } else { 0.5 };
    let adjusted_elapsed = elapsed * water_penalty;
    f64::min((adjusted_elapsed / stage_time).floor(), stages - 1.0)
}

pub fn is_crop_mature(stage: f64, stages: f64) -> bool {
    stage >= stages - 1.0
}

/// Returns one of [`crop_qualities`].
pub fn calculate_crop_quality(watered: bool, fertilized: bool, days_without_water: f64) -> String {
    let mut quality_score = 0.0;
    if watered {
        quality_score += 2.0;
    }
    if fertilized {
        quality_score += 3.0;
    }
    quality_score -= days_without_water;

    if quality_score >= 4.0 {
        return crop_qualities::IRIDIUM.to_owned();
    }
    if quality_score >= 2.0 {
        return crop_qualities::GOLD.to_owned();
    }
    if quality_score >= 1.0 {
        return crop_qualities::SILVER.to_owned();
    }
    crop_qualities::NORMAL.to_owned()
}

/// Returns one of [`crop_mutations`] or `None`. Always draws exactly one float when a definition
/// is given.
pub fn roll_mutation(definition: Option<&CropDefinition>, quality: &str, rng: &mut dyn RandomSource) -> Option<String> {
    let definition = definition?;
    // `definition.mutationChance || 0`
    let base_chance = match definition.mutation_chance {
        Some(chance) if chance != 0.0 && !chance.is_nan() => chance,
        _ => 0.0,
    };
    let quality_bonus = if quality == crop_qualities::IRIDIUM {
        2.0
    } else if quality == crop_qualities::GOLD {
        1.5
    } else if quality == crop_qualities::SILVER {
        1.2
    } else {
        1.0
    };
    let final_chance = base_chance * quality_bonus;

    let roll = rng.float();
    if roll < final_chance * 0.1 {
        return Some(crop_mutations::ANCIENT.to_owned());
    }
    if roll < final_chance * 0.3 {
        return Some(crop_mutations::GOLDEN.to_owned());
    }
    if roll < final_chance {
        return Some(crop_mutations::GIANT.to_owned());
    }
    None
}

pub fn roll_yield(
    definition: Option<&CropDefinition>,
    quality: &str,
    mutation: Option<&str>,
    rng: &mut dyn RandomSource,
) -> f64 {
    let Some(definition) = definition else {
        return 1.0;
    };
    let base_yield = rng.int(definition.yield_min, definition.yield_max);
    let quality_bonus = if quality == crop_qualities::IRIDIUM {
        1.5
    } else if quality == crop_qualities::GOLD {
        1.3
    } else if quality == crop_qualities::SILVER {
        1.1
    } else {
        1.0
    };
    let mutation_bonus = match mutation {
        Some(crop_mutations::ANCIENT) => 3.0,
        Some(crop_mutations::GOLDEN) => 2.5,
        Some(crop_mutations::GIANT) => 2.0,
        _ => 1.0,
    };
    (base_yield * quality_bonus * mutation_bonus).floor()
}

pub fn calculate_harvest_value(
    definition: Option<&CropDefinition>,
    quality: &str,
    mutation: Option<&str>,
    quantity: f64,
) -> f64 {
    let Some(definition) = definition else {
        return 0.0;
    };
    // Unknown keys read `undefined` in JS → NaN through the multiplication.
    let quality_multiplier = content_builtin::quality_multipliers().get(quality).copied().unwrap_or(f64::NAN);
    let mutation_key = match mutation {
        Some(mutation) if !mutation.is_empty() => mutation,
        _ => "none",
    };
    let mutation_multiplier = content_builtin::mutation_multipliers().get(mutation_key).copied().unwrap_or(f64::NAN);
    (definition.base_harvest_value * quality_multiplier * mutation_multiplier * quantity).floor()
}

pub fn can_grow_in_season(definition: Option<&CropDefinition>, season: &str) -> bool {
    let Some(definition) = definition else {
        return false;
    };
    definition.seasons.iter().any(|entry| entry == season)
}

/// TS `SEASON_ORDER[Math.floor(gameDay / DAYS_PER_SEASON) % SEASON_ORDER.length]`, which reads
/// `undefined` for a negative day. This signature cannot say `undefined`, so a negative day
/// yields `""`; [`try_get_current_season`] keeps the JS shape.
pub fn get_current_season(game_day: f64) -> String {
    try_get_current_season(game_day).unwrap_or_default()
}

/// JS-faithful `getCurrentSeason`: `None` where JS reads `undefined` (negative index).
pub fn try_get_current_season(game_day: f64) -> Option<String> {
    let season_count = content_builtin::SEASON_ORDER.len() as f64;
    let season_index = (game_day / content_builtin::DAYS_PER_SEASON).floor() % season_count;
    // JS returns undefined for a negative index; mirror with a bounds check.
    if season_index >= 0.0 && season_index < season_count {
        Some(content_builtin::SEASON_ORDER[season_index as usize].to_owned())
    } else {
        None
    }
}

pub fn get_day_in_season(game_day: f64) -> f64 {
    (game_day % content_builtin::DAYS_PER_SEASON) + 1.0
}

/// In-game days to maturity for a definition (with legacy-ms fallback).
pub fn crop_growth_days(definition: &CropDefinition) -> f64 {
    if let Some(days) = definition.growth_days {
        if days > 0.0 {
            return days;
        }
    }
    // `Math.min(28, Math.max(1, x))`; clamp keeps a NaN input NaN like the JS does.
    js::round(definition.growth_time / 5000.0).clamp(1.0, 28.0)
}

/// In-game days between repeat harvests for regrowing crops.
pub fn crop_regrowth_days(definition: &CropDefinition) -> f64 {
    if let Some(days) = definition.regrowth_days {
        if days > 0.0 {
            return days;
        }
    }
    // `if (definition.regrowthTime)` — 0 and NaN are falsy.
    if let Some(time) = definition.regrowth_time {
        if time != 0.0 && !time.is_nan() {
            return js::round(time / 5000.0).clamp(1.0, 28.0);
        }
    }
    1.0
}

/// Visual growth stage for the day-based model (0 .. stages-1).
pub fn compute_crop_stage(crop: &Crop, definition: &CropDefinition) -> f64 {
    let growth_days = crop_growth_days(definition);
    let days_grown = crop.days_grown.unwrap_or(0.0);
    let progress = f64::min(1.0, days_grown / growth_days);
    f64::min((progress * (definition.stages - 1.0)).floor(), definition.stages - 1.0)
}

/// Mature when it has accumulated enough watered days.
pub fn is_crop_mature_by_days(crop: &Crop, definition: &CropDefinition) -> bool {
    if crop.withered == Some(true) {
        return false;
    }
    crop.days_grown.unwrap_or(0.0) >= crop_growth_days(definition)
}

/// Day-based planting (M2+): crops start unwatered — water them or they won't grow.
pub fn create_planted_crop(crop_type: &str, planted_on_day: f64, fertilized: bool) -> Crop {
    Crop {
        r#type: crop_type.to_owned(),
        planted_at: 0.0,
        planted_on_day: Some(planted_on_day),
        days_grown: Some(0.0),
        stage: 0.0,
        watered: false,
        last_watered_day: None,
        quality: if fertilized { crop_qualities::SILVER.to_owned() } else { crop_qualities::NORMAL.to_owned() },
        mutation: None,
        harvest_count: 0.0,
        days_without_water: 0.0,
        ..Crop::default()
    }
}

pub fn initialize_crop(crop_type: &str, planted_at: f64, fertilized: bool) -> Crop {
    Crop {
        r#type: crop_type.to_owned(),
        planted_at,
        stage: 0.0,
        watered: true,
        last_watered_day: Some(0.0),
        quality: if fertilized { crop_qualities::SILVER.to_owned() } else { crop_qualities::NORMAL.to_owned() },
        mutation: None,
        harvest_count: 0.0,
        days_without_water: 0.0,
        ..Crop::default()
    }
}

/// Bounds come from `tiles.length` and `tiles[0].length` like the TS; a ragged grid panics on
/// the row index exactly where the TS reads `undefined.type` (and the C# throws).
pub fn can_place_multi_tile_crop(tiles: &[Vec<Tile>], x: f64, y: f64, width: f64, height: f64) -> bool {
    let mut dy = 0.0;
    while dy < height {
        let mut dx = 0.0;
        while dx < width {
            let check_x = x + dx;
            let check_y = y + dy;
            if check_y < 0.0 || check_y >= tiles.len() as f64 || check_x < 0.0 || check_x >= tiles[0].len() as f64 {
                return false;
            }
            let tile = &tiles[check_y as usize][check_x as usize];
            if tile.r#type != tile_types::SOIL || tile.crop.is_some() {
                return false;
            }
            dx += 1.0;
        }
        dy += 1.0;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stable_json;

    #[test]
    fn custom_crops_override_builtins_by_id_and_keep_custom_asset() {
        let custom = CustomCropDefinition {
            id: "wheat".to_owned(),
            name: "Blue Wheat".to_owned(),
            custom_asset: Some("asset-1".to_owned()),
            ..CustomCropDefinition::default()
        };
        let merged = merge_custom_crop_definitions(std::slice::from_ref(&custom));
        assert_eq!(merged.len(), content_builtin::crop_definitions().len());
        assert_eq!(merged.get_index_of("wheat"), Some(0));
        assert_eq!(merged["wheat"].name, "Blue Wheat");
        assert_eq!(merged["wheat"].extra.get("customAsset"), Some(&Value::String("asset-1".to_owned())));
        // The JSON shapes are identical: the field just moves between typed and extra.
        assert_eq!(stable_json::stringify(&merged["wheat"]), stable_json::stringify(&custom));
        assert_eq!(to_custom_crop_definition(&merged["wheat"]), custom);
    }
}

/// Port of tests/unit/crops.characterization.test.ts (`CropsCharacterizationTests.cs`).
/// src/lib/crops.ts is a thin wrapper over the Core farming math; the wrappers are reproduced
/// here as local helpers (`getCropDefinition(cropType, customCrops)` →
/// [`merge_custom_crop_definitions`], the Math.random-backed `legacyRandom` → a pinned
/// [`RandomSource`]).
#[cfg(test)]
mod characterization_tests {
    use super::*;
    use crate::stable_json;

    /// TS legacyRandom with Math.random pinned to a fixed roll; counts draws.
    struct PinnedRandom {
        value: f64,
        draws: usize,
    }

    impl PinnedRandom {
        fn new(value: f64) -> Self {
            Self { value, draws: 0 }
        }
    }

    impl RandomSource for PinnedRandom {
        fn float(&mut self) -> f64 {
            self.draws += 1;
            self.value
        }

        fn int(&mut self, min: f64, max: f64) -> f64 {
            self.draws += 1;
            (self.value * (max - min + 1.0)).floor() + min
        }
    }

    fn make_custom_crop(id: &str) -> CustomCropDefinition {
        CustomCropDefinition {
            id: id.to_owned(),
            name: "Custom".to_owned(),
            seed_cost: 5.0,
            base_harvest_value: 100.0,
            growth_time: 10000.0,
            stages: 5.0,
            seasons: vec!["winter".to_owned()],
            can_regrow: false,
            yield_min: 1.0,
            yield_max: 1.0,
            mutation_chance: Some(0.5),
            ..CustomCropDefinition::default()
        }
    }

    fn named(id: &str, name: &str) -> CustomCropDefinition {
        CustomCropDefinition { name: name.to_owned(), ..make_custom_crop(id) }
    }

    fn with_value(id: &str, name: &str, base_harvest_value: f64) -> CustomCropDefinition {
        CustomCropDefinition { base_harvest_value, ..named(id, name) }
    }

    fn with_chance(id: &str, mutation_chance: f64) -> CustomCropDefinition {
        CustomCropDefinition { mutation_chance: Some(mutation_chance), ..make_custom_crop(id) }
    }

    fn with_seasons(id: &str, seasons: &[&str]) -> CustomCropDefinition {
        CustomCropDefinition { seasons: seasons.iter().map(|s| (*s).to_owned()).collect(), ..make_custom_crop(id) }
    }

    fn get_all_crop_definitions(custom_crops: &[CustomCropDefinition]) -> IndexMap<String, CropDefinition> {
        merge_custom_crop_definitions(custom_crops)
    }

    fn get_crop_definition(crop_type: &str, custom_crops: &[CustomCropDefinition]) -> Option<CropDefinition> {
        get_all_crop_definitions(custom_crops).get(crop_type).cloned()
    }

    fn check_for_mutation(
        crop_type: &str,
        quality: &str,
        roll: f64,
        custom_crops: &[CustomCropDefinition],
    ) -> Option<String> {
        roll_mutation(get_crop_definition(crop_type, custom_crops).as_ref(), quality, &mut PinnedRandom::new(roll))
    }

    fn calculate_yield(crop_type: &str, quality: &str, mutation: Option<&str>, roll: f64) -> f64 {
        roll_yield(get_crop_definition(crop_type, &[]).as_ref(), quality, mutation, &mut PinnedRandom::new(roll))
    }

    fn harvest_value(
        crop_type: &str,
        quality: &str,
        mutation: Option<&str>,
        quantity: f64,
        custom_crops: &[CustomCropDefinition],
    ) -> f64 {
        calculate_harvest_value(get_crop_definition(crop_type, custom_crops).as_ref(), quality, mutation, quantity)
    }

    fn grows_in_season(crop_type: &str, season: &str, custom_crops: &[CustomCropDefinition]) -> bool {
        can_grow_in_season(get_crop_definition(crop_type, custom_crops).as_ref(), season)
    }

    /// h rows by w cols grid of empty soil tiles.
    fn soil_grid(w: usize, h: usize) -> Vec<Vec<Tile>> {
        (0..h).map(|_| (0..w).map(|_| Tile { r#type: "soil".to_owned(), ..Tile::default() }).collect()).collect()
    }

    // --- exported constants ---

    #[test]
    fn quality_multipliers_values() {
        let actual: Vec<(String, f64)> = content_builtin::quality_multipliers().into_iter().collect();
        let expected: Vec<(String, f64)> = [("normal", 1.0), ("silver", 1.25), ("gold", 1.5), ("iridium", 2.0)]
            .into_iter()
            .map(|(k, v)| (k.to_owned(), v))
            .collect();
        assert_eq!(actual, expected);
    }

    #[test]
    fn mutation_multipliers_values() {
        let actual: Vec<(String, f64)> = content_builtin::mutation_multipliers().into_iter().collect();
        let expected: Vec<(String, f64)> = [("none", 1.0), ("giant", 2.5), ("golden", 3.0), ("ancient", 4.0)]
            .into_iter()
            .map(|(k, v)| (k.to_owned(), v))
            .collect();
        assert_eq!(actual, expected);
    }

    #[test]
    fn days_per_season_is_28() {
        assert_eq!(content_builtin::DAYS_PER_SEASON, 28.0);
    }

    #[test]
    fn season_order_is_spring_summer_fall_winter() {
        assert_eq!(content_builtin::SEASON_ORDER, &["spring", "summer", "fall", "winter"]);
    }

    #[test]
    fn crop_definitions_contains_exactly_the_9_built_in_crops_keyed_by_id() {
        let definitions = content_builtin::crop_definitions();
        let mut keys: Vec<&str> = definitions.keys().map(String::as_str).collect();
        keys.sort_by(|a, b| js::compare_strings(a, b));
        assert_eq!(
            keys,
            ["blueberry", "carrot", "cauliflower", "corn", "potato", "pumpkin", "strawberry", "tomato", "wheat"]
        );
        for (key, definition) in &definitions {
            assert_eq!(key, &definition.id);
        }
    }

    #[test]
    fn wheat_built_in_definition_is_pinned_exactly() {
        assert_eq!(
            stable_json::stringify(&content_builtin::crop_definitions()["wheat"]),
            r#"{"baseHarvestValue":25,"canRegrow":false,"growthDays":3,"growthTime":15000,"id":"wheat","mutationChance":0.01,"name":"Wheat","seasons":["spring","fall"],"seedCost":10,"stages":4,"yieldMax":2,"yieldMin":1}"#
        );
    }

    // --- getAllCropDefinitions / getCropDefinition ---

    #[test]
    fn returns_only_the_9_built_ins_with_no_custom_crops() {
        let all = get_all_crop_definitions(&[]);
        assert_eq!(all.len(), 9);
        assert_eq!(
            stable_json::stringify(&content_builtin::crop_definitions()["wheat"]),
            stable_json::stringify(&all["wheat"])
        );
    }

    #[test]
    fn merges_in_custom_crops_under_their_id() {
        let all = get_all_crop_definitions(&[named("moonberry", "Moonberry")]);
        assert_eq!(all.len(), 10);
        assert_eq!(all["moonberry"].name, "Moonberry");
    }

    #[test]
    fn custom_crop_with_a_built_in_id_overrides_the_built_in_definition() {
        let all = get_all_crop_definitions(&[with_value("wheat", "Evil Wheat", 999.0)]);
        assert_eq!(all.len(), 9);
        assert_eq!(all["wheat"].name, "Evil Wheat");
        assert_eq!(all["wheat"].base_harvest_value, 999.0);
    }

    #[test]
    fn get_crop_definition_resolves_built_ins_unknowns_and_overrides() {
        assert_eq!(get_crop_definition("corn", &[]), content_builtin::crop_definitions().get("corn").cloned());
        assert_eq!(get_crop_definition("nope", &[]), None);
        let custom = [make_custom_crop("moonberry"), named("wheat", "Evil Wheat")];
        assert_eq!(get_crop_definition("moonberry", &custom).map(|d| d.id), Some("moonberry".to_owned()));
        assert_eq!(get_crop_definition("wheat", &custom).map(|d| d.name), Some("Evil Wheat".to_owned()));
    }

    // --- getCropStage (wheat-like numbers: growthTime 15000, stages 4 → stageTime 3750) ---

    #[test]
    fn get_crop_stage_pins_its_behavior() {
        assert_eq!(get_crop_stage(0.0, 0.0, 15000.0, 4.0, true), 0.0);
        assert_eq!(get_crop_stage(0.0, 3749.0, 15000.0, 4.0, true), 0.0);
        assert_eq!(get_crop_stage(0.0, 3750.0, 15000.0, 4.0, true), 1.0);
        // clamps to stages - 1 at and beyond full growth time
        assert_eq!(get_crop_stage(0.0, 15000.0, 15000.0, 4.0, true), 3.0);
        assert_eq!(get_crop_stage(0.0, 1_000_000.0, 15000.0, 4.0, true), 3.0);
        // unwatered halves effective elapsed time (0.5 penalty)
        assert_eq!(get_crop_stage(0.0, 7500.0, 15000.0, 4.0, true), 2.0);
        assert_eq!(get_crop_stage(0.0, 7500.0, 15000.0, 4.0, false), 1.0);
        assert_eq!(get_crop_stage(0.0, 15000.0, 15000.0, 4.0, false), 2.0);
        assert_eq!(get_crop_stage(0.0, 30000.0, 15000.0, 4.0, false), 3.0);
        // QUIRK: currentTime before plantedAt yields a NEGATIVE stage (no lower clamp)
        assert_eq!(get_crop_stage(1000.0, 0.0, 15000.0, 4.0, true), -1.0);
    }

    #[test]
    fn is_mature_exactly_when_stage_is_at_least_stages_minus_one() {
        assert!(is_crop_mature(3.0, 4.0));
        assert!(!is_crop_mature(2.0, 4.0));
        assert!(is_crop_mature(4.0, 4.0)); // over-shoot still mature
        assert!(is_crop_mature(0.0, 1.0));
    }

    // --- calculateCropQuality: score = (watered ? 2 : 0) + (fertilized ? 3 : 0) - daysWithoutWater ---

    #[test]
    fn calculate_crop_quality_boundaries() {
        let cases: [(bool, bool, f64, &str); 11] = [
            (true, true, 0.0, "iridium"),
            (true, true, 1.0, "iridium"),
            (true, true, 2.0, "gold"),
            (true, true, 3.0, "gold"),
            (true, true, 4.0, "silver"),
            (true, true, 5.0, "normal"),
            (true, false, 0.0, "gold"),
            (true, false, 1.0, "silver"),
            (true, false, 2.0, "normal"),
            (false, true, 0.0, "gold"), // QUIRK: fertilized only caps at gold
            (false, false, 0.0, "normal"),
        ];
        for (watered, fertilized, days_without_water, expected) in cases {
            assert_eq!(
                calculate_crop_quality(watered, fertilized, days_without_water),
                expected,
                "watered={watered} fertilized={fertilized} days={days_without_water}"
            );
        }
    }

    // --- checkForMutation ---

    #[test]
    fn roll_bands_for_a_half_chance_crop_at_normal_quality() {
        let cases: [(f64, Option<&str>); 8] = [
            (0.0, Some("ancient")),
            (0.0499, Some("ancient")),
            (0.05, Some("golden")), // exact ancient boundary falls through to golden
            (0.1, Some("golden")),
            (0.15, Some("giant")), // exact golden boundary falls through to giant
            (0.4999, Some("giant")),
            (0.5, None), // exact chance boundary is NOT a mutation
            (0.999, None),
        ];
        for (roll, expected) in cases {
            assert_eq!(
                check_for_mutation("mutey", "normal", roll, &[with_chance("mutey", 0.5)]).as_deref(),
                expected,
                "roll={roll}"
            );
        }
    }

    #[test]
    fn quality_bonus_scales_the_chance() {
        // wheat baseChance 0.01
        assert_eq!(check_for_mutation("wheat", "silver", 0.0119, &[]).as_deref(), Some("giant"));
        assert_eq!(check_for_mutation("wheat", "normal", 0.0119, &[]), None);
        assert_eq!(check_for_mutation("wheat", "gold", 0.0149, &[]).as_deref(), Some("giant"));
        assert_eq!(check_for_mutation("wheat", "silver", 0.0149, &[]), None);
        assert_eq!(check_for_mutation("wheat", "iridium", 0.0199, &[]).as_deref(), Some("giant"));
        assert_eq!(check_for_mutation("wheat", "gold", 0.0199, &[]), None);
    }

    #[test]
    fn a_zero_mutation_chance_crop_never_mutates_even_on_roll_0() {
        assert_eq!(check_for_mutation("dud", "iridium", 0.0, &[with_chance("dud", 0.0)]), None);
    }

    #[test]
    fn unknown_crop_type_mutation_returns_null_without_consuming_a_random_roll() {
        let mut rng = PinnedRandom::new(0.0);
        assert_eq!(roll_mutation(get_crop_definition("nope", &[]).as_ref(), "iridium", &mut rng), None);
        assert_eq!(rng.draws, 0);
    }

    // --- calculateYield ---

    #[test]
    fn base_yield_roll_spans_yield_min_to_yield_max_inclusive() {
        let cases: [(&str, f64, f64); 6] = [
            ("wheat", 0.0, 1.0),
            ("wheat", 0.4999, 1.0),
            ("wheat", 0.5, 2.0),
            ("wheat", 0.9999, 2.0),
            ("blueberry", 0.0, 2.0),
            ("blueberry", 0.9999, 5.0),
        ];
        for (crop, roll, expected) in cases {
            assert_eq!(calculate_yield(crop, "normal", None, roll), expected, "crop={crop} roll={roll}");
        }
    }

    #[test]
    fn quality_and_mutation_bonuses_are_floored_and_compound() {
        // quality bonus is floored away on small yields (base 1)
        assert_eq!(calculate_yield("wheat", "silver", None, 0.0), 1.0);
        assert_eq!(calculate_yield("wheat", "gold", None, 0.0), 1.0);
        assert_eq!(calculate_yield("wheat", "iridium", None, 0.0), 1.0);
        // quality bonus applies on larger base yields (base 2)
        assert_eq!(calculate_yield("wheat", "silver", None, 0.9999), 2.0);
        assert_eq!(calculate_yield("wheat", "gold", None, 0.9999), 2.0);
        assert_eq!(calculate_yield("wheat", "iridium", None, 0.9999), 3.0);
        // mutation bonus: giant x2, golden x2.5, ancient x3 (pumpkin base yield always 1)
        assert_eq!(calculate_yield("pumpkin", "normal", Some("giant"), 0.0), 2.0);
        assert_eq!(calculate_yield("pumpkin", "normal", Some("golden"), 0.0), 2.0);
        assert_eq!(calculate_yield("pumpkin", "normal", Some("ancient"), 0.0), 3.0);
        // compound
        assert_eq!(calculate_yield("wheat", "iridium", Some("ancient"), 0.9999), 9.0);
    }

    #[test]
    fn unknown_crop_type_yield_returns_1_without_consuming_a_random_roll() {
        let mut rng = PinnedRandom::new(0.9999);
        assert_eq!(roll_yield(get_crop_definition("nope", &[]).as_ref(), "iridium", Some("ancient"), &mut rng), 1.0);
        assert_eq!(rng.draws, 0);
    }

    // --- calculateHarvestValue ---

    #[test]
    fn calculate_harvest_value_pins_its_behavior() {
        assert_eq!(harvest_value("wheat", "normal", None, 1.0, &[]), 25.0);
        assert_eq!(harvest_value("wheat", "silver", None, 1.0, &[]), 31.0);
        assert_eq!(harvest_value("wheat", "gold", None, 1.0, &[]), 37.0);
        assert_eq!(harvest_value("wheat", "iridium", None, 1.0, &[]), 50.0);
        assert_eq!(harvest_value("wheat", "normal", Some("giant"), 1.0, &[]), 62.0);
        assert_eq!(harvest_value("wheat", "normal", Some("golden"), 1.0, &[]), 75.0);
        assert_eq!(harvest_value("wheat", "normal", Some("ancient"), 1.0, &[]), 100.0);
        assert_eq!(harvest_value("wheat", "iridium", Some("ancient"), 3.0, &[]), 600.0);
        assert_eq!(harvest_value("wheat", "iridium", Some("ancient"), 0.0, &[]), 0.0);
        assert_eq!(harvest_value("nope", "iridium", Some("ancient"), 5.0, &[]), 0.0);
        assert_eq!(harvest_value("wheat", "normal", None, 1.0, &[with_value("wheat", "Custom", 1000.0)]), 1000.0);
    }

    #[test]
    fn unknown_quality_or_mutation_keys_read_nan_like_javascript() {
        assert!(harvest_value("wheat", "legendary", None, 1.0, &[]).is_nan());
        assert!(harvest_value("wheat", "normal", Some("cursed"), 1.0, &[]).is_nan());
    }

    // --- canGrowInSeason ---

    #[test]
    fn can_grow_in_season_matches_the_definition_seasons_list() {
        assert!(grows_in_season("wheat", "spring", &[]));
        assert!(grows_in_season("wheat", "fall", &[]));
        assert!(!grows_in_season("wheat", "summer", &[]));
        assert!(!grows_in_season("wheat", "winter", &[]));
        assert!(grows_in_season("carrot", "winter", &[]));
        assert!(!grows_in_season("nope", "spring", &[]));
        let custom = [with_seasons("wheat", &["winter"])];
        assert!(!grows_in_season("wheat", "spring", &custom));
        assert!(grows_in_season("wheat", "winter", &custom));
    }

    // --- getCurrentSeason / getDayInSeason ---

    #[test]
    fn game_day_is_zero_based_season_rolls_over_at_day_28() {
        let cases: [(f64, Option<&str>); 10] = [
            (0.0, Some("spring")),
            (27.0, Some("spring")),
            (28.0, Some("summer")),
            (55.0, Some("summer")),
            (56.0, Some("fall")),
            (83.0, Some("fall")),
            (84.0, Some("winter")),
            (111.0, Some("winter")),
            (112.0, Some("spring")), // full year wraps
            (-1.0, None),            // QUIRK: negative gameDay gives undefined season
        ];
        for (day, season) in cases {
            assert_eq!(try_get_current_season(day).as_deref(), season, "day={day}");
            assert_eq!(get_current_season(day), season.unwrap_or(""), "day={day}");
        }
    }

    #[test]
    fn get_day_in_season_is_one_based_within_a_28_day_season() {
        let cases: [(f64, f64); 6] = [(0.0, 1.0), (27.0, 28.0), (28.0, 1.0), (55.0, 28.0), (112.0, 1.0), (-1.0, 0.0)];
        for (day, expected) in cases {
            // QUIRK: (-1 % 28) + 1 = 0
            assert_eq!(get_day_in_season(day), expected, "day={day}");
        }
    }

    // --- initializeCrop ---

    #[test]
    fn unfertilized_crop_starts_normal_quality_with_all_counters_zeroed() {
        assert_eq!(
            stable_json::stringify(&initialize_crop("wheat", 12345.0, false)),
            r#"{"daysWithoutWater":0,"harvestCount":0,"lastWateredDay":0,"mutation":null,"plantedAt":12345,"quality":"normal","stage":0,"type":"wheat","watered":true}"#
        );
    }

    #[test]
    fn fertilized_crop_starts_at_silver_quality_and_types_are_unvalidated() {
        assert_eq!(initialize_crop("tomato", 0.0, true).quality, "silver");
        assert_eq!(initialize_crop("not-a-real-crop", 7.0, false).r#type, "not-a-real-crop");
    }

    // --- createPlantedCrop / day-based helpers ---

    #[test]
    fn planted_crop_starts_unwatered_on_its_planting_day() {
        assert_eq!(
            stable_json::stringify(&create_planted_crop("wheat", 3.0, false)),
            r#"{"daysGrown":0,"daysWithoutWater":0,"harvestCount":0,"mutation":null,"plantedAt":0,"plantedOnDay":3,"quality":"normal","stage":0,"type":"wheat","watered":false}"#
        );
        assert_eq!(create_planted_crop("wheat", 3.0, true).quality, "silver");
    }

    #[test]
    fn growth_days_prefer_the_day_field_and_fall_back_to_legacy_ms() {
        let wheat = &content_builtin::crop_definitions()["wheat"];
        assert_eq!(crop_growth_days(wheat), 3.0);
        let legacy = CropDefinition { growth_days: None, growth_time: 15000.0, ..wheat.clone() };
        assert_eq!(crop_growth_days(&legacy), 3.0);
        let tiny = CropDefinition { growth_days: None, growth_time: 1.0, ..wheat.clone() };
        assert_eq!(crop_growth_days(&tiny), 1.0);
        let huge = CropDefinition { growth_days: None, growth_time: 1_000_000.0, ..wheat.clone() };
        assert_eq!(crop_growth_days(&huge), 28.0);
    }

    #[test]
    fn regrowth_days_prefer_the_day_field_then_legacy_ms_then_one() {
        let tomato = &content_builtin::crop_definitions()["tomato"];
        assert_eq!(crop_regrowth_days(tomato), 2.0);
        let legacy = CropDefinition { regrowth_days: None, regrowth_time: Some(20000.0), ..tomato.clone() };
        assert_eq!(crop_regrowth_days(&legacy), 4.0);
        let zero = CropDefinition { regrowth_days: None, regrowth_time: Some(0.0), ..tomato.clone() };
        assert_eq!(crop_regrowth_days(&zero), 1.0);
        let none = CropDefinition { regrowth_days: None, regrowth_time: None, ..tomato.clone() };
        assert_eq!(crop_regrowth_days(&none), 1.0);
    }

    #[test]
    fn stage_and_maturity_follow_watered_days() {
        let wheat = &content_builtin::crop_definitions()["wheat"];
        let mut crop = create_planted_crop("wheat", 1.0, false);
        assert_eq!(compute_crop_stage(&crop, wheat), 0.0);
        assert!(!is_crop_mature_by_days(&crop, wheat));
        crop.days_grown = Some(2.0);
        assert_eq!(compute_crop_stage(&crop, wheat), 2.0);
        assert!(!is_crop_mature_by_days(&crop, wheat));
        crop.days_grown = Some(3.0);
        assert_eq!(compute_crop_stage(&crop, wheat), 3.0);
        assert!(is_crop_mature_by_days(&crop, wheat));
        crop.days_grown = Some(99.0);
        assert_eq!(compute_crop_stage(&crop, wheat), 3.0);
        crop.withered = Some(true);
        assert!(!is_crop_mature_by_days(&crop, wheat));
    }

    // --- canPlaceMultiTileCrop ---

    #[test]
    fn can_place_multi_tile_crop_pins_its_behavior() {
        assert!(can_place_multi_tile_crop(&soil_grid(4, 4), 0.0, 0.0, 2.0, 2.0));
        assert!(can_place_multi_tile_crop(&soil_grid(4, 4), 2.0, 2.0, 2.0, 2.0)); // flush with edge
        assert!(!can_place_multi_tile_crop(&soil_grid(4, 4), 3.0, 0.0, 2.0, 2.0)); // off right edge
        assert!(!can_place_multi_tile_crop(&soil_grid(4, 4), 0.0, 3.0, 2.0, 2.0)); // off bottom edge
        assert!(!can_place_multi_tile_crop(&soil_grid(4, 4), -1.0, 0.0, 2.0, 2.0));
        assert!(!can_place_multi_tile_crop(&soil_grid(4, 4), 0.0, -1.0, 2.0, 2.0));

        let mut occupied = soil_grid(4, 4);
        occupied[1][1].crop = Some(Crop { r#type: "wheat".to_owned(), ..Crop::default() });
        assert!(!can_place_multi_tile_crop(&occupied, 0.0, 0.0, 2.0, 2.0));
        assert!(can_place_multi_tile_crop(&occupied, 2.0, 2.0, 2.0, 2.0)); // clear region still fine

        let mut grass = soil_grid(4, 4);
        grass[0][1].r#type = "grass".to_owned();
        assert!(!can_place_multi_tile_crop(&grass, 0.0, 0.0, 2.0, 2.0));

        // QUIRK: zero-sized footprint is always placeable, even out of bounds
        assert!(can_place_multi_tile_crop(&soil_grid(2, 2), 99.0, 99.0, 0.0, 0.0));
    }

    #[test]
    #[should_panic(expected = "index out of bounds")]
    fn quirk_bounds_use_the_first_row_length_so_ragged_grids_throw() {
        let soil = || Tile { r#type: "soil".to_owned(), ..Tile::default() };
        let ragged = vec![vec![soil(), soil()], vec![soil()]];
        can_place_multi_tile_crop(&ragged, 0.0, 0.0, 2.0, 2.0);
    }
}
