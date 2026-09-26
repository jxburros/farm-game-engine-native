//! Farming math (port of `Farming/Crops.cs`, engine-core/src/farming/crops.ts). This phase
//! ports the content-assembly part (`mergeCropDefinitions`); the growth, quality and yield
//! functions arrive with the gameplay modules.

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

use crate::rng::RandomSource;
use crate::schema::{Crop, GameContent, Tile};

pub fn get_crop_definition_from_content<'a>(content: &'a GameContent, crop_type: &str) -> Option<&'a CropDefinition> {
    let _ = (content, crop_type);
    todo!("port Crops.GetCropDefinitionFromContent")
}

pub fn get_crop_stage(planted_at: f64, current_time: f64, growth_time: f64, stages: f64, watered: bool) -> f64 {
    let _ = (planted_at, current_time, growth_time, stages, watered);
    todo!("port Crops.GetCropStage")
}

pub fn is_crop_mature(stage: f64, stages: f64) -> bool {
    let _ = (stage, stages);
    todo!("port Crops.IsCropMature")
}

pub fn calculate_crop_quality(watered: bool, fertilized: bool, days_without_water: f64) -> String {
    let _ = (watered, fertilized, days_without_water);
    todo!("port Crops.CalculateCropQuality")
}

pub fn roll_mutation(definition: Option<&CropDefinition>, quality: &str, rng: &mut dyn RandomSource) -> Option<String> {
    let _ = (definition, quality, rng);
    todo!("port Crops.RollMutation")
}

pub fn roll_yield(
    definition: Option<&CropDefinition>,
    quality: &str,
    mutation: Option<&str>,
    rng: &mut dyn RandomSource,
) -> f64 {
    let _ = (definition, quality, mutation, rng);
    todo!("port Crops.RollYield")
}

pub fn calculate_harvest_value(
    definition: Option<&CropDefinition>,
    quality: &str,
    mutation: Option<&str>,
    quantity: f64,
) -> f64 {
    let _ = (definition, quality, mutation, quantity);
    todo!("port Crops.CalculateHarvestValue")
}

pub fn can_grow_in_season(definition: Option<&CropDefinition>, season: &str) -> bool {
    let _ = (definition, season);
    todo!("port Crops.CanGrowInSeason")
}

pub fn get_current_season(game_day: f64) -> String {
    let _ = game_day;
    todo!("port Crops.GetCurrentSeason")
}

pub fn get_day_in_season(game_day: f64) -> f64 {
    let _ = game_day;
    todo!("port Crops.GetDayInSeason")
}

pub fn crop_growth_days(definition: &CropDefinition) -> f64 {
    let _ = definition;
    todo!("port Crops.CropGrowthDays")
}

pub fn crop_regrowth_days(definition: &CropDefinition) -> f64 {
    let _ = definition;
    todo!("port Crops.CropRegrowthDays")
}

pub fn compute_crop_stage(crop: &Crop, definition: &CropDefinition) -> f64 {
    let _ = (crop, definition);
    todo!("port Crops.ComputeCropStage")
}

pub fn is_crop_mature_by_days(crop: &Crop, definition: &CropDefinition) -> bool {
    let _ = (crop, definition);
    todo!("port Crops.IsCropMatureByDays")
}

pub fn create_planted_crop(crop_type: &str, planted_on_day: f64, fertilized: bool) -> Crop {
    let _ = (crop_type, planted_on_day, fertilized);
    todo!("port Crops.CreatePlantedCrop")
}

pub fn initialize_crop(crop_type: &str, planted_at: f64, fertilized: bool) -> Crop {
    let _ = (crop_type, planted_at, fertilized);
    todo!("port Crops.InitializeCrop")
}

pub fn can_place_multi_tile_crop(tiles: &[Vec<Tile>], x: f64, y: f64, width: f64, height: f64) -> bool {
    let _ = (tiles, x, y, width, height);
    todo!("port Crops.CanPlaceMultiTileCrop")
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
