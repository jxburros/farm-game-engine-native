//! Farming & tool interactions (M2): energy costs, day-based growth, gathering-node strikes,
//! tiered area-of-effect, explicit selling (harvest no longer auto-pays). (Port of
//! `Farming/FarmingActions.cs` / engine-core/src/farming/actions.ts.)

use crate::effects::{message_levels, Effect};
use crate::engine_types::{Effects, EngineContext};
use crate::events::EventPosition;
use crate::farming::crops;
use crate::hooks::{CropHarvestHookPayload, HookEvent, NpcInteractHookPayload};
use crate::rng::Rng;
use crate::schema::{
    crop_qualities, item_types, soil_states, tile_types, tool_types, Crop, DialogueState, GameState, Item, Tile,
    FISHING_MINIGAME_ID,
};
use crate::world::world_movement;
use crate::{animals, crafting, energy, events, fishing, gathering, inventory, mines, quests, skills, social, tools};
use indexmap::IndexMap;
use serde_json::Value;

/// TS `facingTile` result. The C# record holds the `Scene`; here the scene's identity and size
/// are copied out (and the tile cloned) so the caller can go on to mutate the state.
struct FacingTileResult {
    scene_id: String,
    width: i32,
    height: i32,
    x: i32,
    y: i32,
    tile: Tile,
}

fn facing_tile(state: &GameState) -> Option<FacingTileResult> {
    let scene = world_movement::find_scene(state, &state.player.scene_id)?;
    let vector = world_movement::get_direction_vector(&state.player.direction);
    let origin = world_movement::player_tile(state);
    let x = origin.x + vector.dx;
    let y = origin.y + vector.dy;
    if x < 0 || x >= scene.width || y < 0 || y >= scene.height {
        return None;
    }
    Some(FacingTileResult {
        scene_id: scene.id.clone(),
        width: scene.width,
        height: scene.height,
        x,
        y,
        tile: scene.tiles[y as usize][x as usize].clone(),
    })
}

/// Tiles affected by an AoE-capable tool: facing tile + perpendicular neighbors at tier 2+.
fn aoe_targets(target: &FacingTileResult, direction: &str, tier: i32) -> Vec<(i32, i32)> {
    let mut targets = vec![(target.x, target.y)];
    if tier >= 2 {
        let horizontal = direction == "up" || direction == "down";
        let offsets: [(i32, i32); 2] = if horizontal { [(-1, 0), (1, 0)] } else { [(0, -1), (0, 1)] };
        for (dx, dy) in offsets {
            let tx = target.x + dx;
            let ty = target.y + dy;
            if tx >= 0 && tx < target.width && ty >= 0 && ty < target.height {
                targets.push((tx, ty));
            }
        }
    }
    targets
}

/// TS `waterTileInPlace`.
fn water_tile(tile: &mut Tile, day: u32) {
    if tile.background != tile_types::SOIL {
        return;
    }
    tile.soil_moisture = 100;
    tile.soil_state = Some(
        if tile.soil_state.as_deref() == Some(soil_states::FERTILIZED) {
            soil_states::FERTILIZED
        } else {
            soil_states::WATERED
        }
        .to_owned(),
    );
    if let Some(crop) = &mut tile.crop {
        if crop.withered != Some(true) {
            crop.watered = true;
            crop.last_watered_day = Some(day);
            crop.days_without_water = 0;
        }
    }
}

fn scene_index(state: &GameState, scene_id: &str) -> Option<usize> {
    state.world.scenes.iter().position(|scene| scene.id == scene_id)
}

/// Durability + energy apply after a successful action (the C# local `Finish`).
fn finish(ctx: &EngineContext, state: &mut GameState, tool: &Item, energy_cost: i32, mut effects: Effects) -> Effects {
    state.player.inventory =
        inventory::replace_item(&state.player.inventory, &tool.id, &tools::damage_tool_durability(tool, 1));
    let spent = energy::spend_energy(ctx, state, energy_cost);
    effects.extend(spent.effects);
    effects
}

pub fn handle_use_tool(ctx: &EngineContext, state: &mut GameState, tool_type: &str) -> Effects {
    let Some(tool_slot) = inventory::find_tool_slot(&state.player.inventory, tool_type).cloned() else {
        let name = if tool_type == tool_types::WATERING_CAN {
            "watering can".to_owned()
        } else {
            gathering::replace_first_dash(tool_type)
        };
        return vec![Effect::message(message_levels::ERROR, format!("You need a {name}!"))];
    };
    if tools::is_tool_broken(&tool_slot.item) {
        return vec![Effect::message(
            message_levels::ERROR,
            format!("Your {} is broken! A shop can repair it.", tool_slot.item.name),
        )];
    }

    let Some(target) = facing_tile(state) else {
        return Vec::new();
    };

    let tier = tool_slot.item.tool_tier.unwrap_or(1);
    let power = tool_slot.item.tool_power.unwrap_or(tier);
    let definition = tools::get_tool_definition(tool_type);
    let energy_cost = energy::effective_energy_cost(&definition, tier);
    let tool = &tool_slot.item;

    // Gathering node strike takes priority on node tiles.
    if target.tile.node.is_some() && gathering::is_node_active(&target.tile) {
        let outcome = gathering::strike_node(ctx, state, &target.scene_id, target.x, target.y, tool_type, tier, power);
        if !outcome.struck {
            return outcome.effects;
        }
        return finish(ctx, state, tool, energy_cost, outcome.effects);
    }

    if tool_type == tool_types::WATERING_CAN && target.tile.background == tile_types::SOIL {
        let Some(index) = scene_index(state, &target.scene_id) else {
            return Vec::new();
        };
        let day = state.clock.day;
        let direction = state.player.direction.clone();
        for (spot_x, spot_y) in aoe_targets(&target, &direction, tier) {
            water_tile(&mut state.world.scenes[index].tiles[spot_y as usize][spot_x as usize], day);
        }
        return finish(ctx, state, tool, energy_cost, vec![Effect::message(message_levels::SUCCESS, "Watered!")]);
    }

    if tool_type == tool_types::HOE
        && (target.tile.background == tile_types::GRASS || target.tile.background == tile_types::FLOOR)
    {
        let Some(index) = scene_index(state, &target.scene_id) else {
            return Vec::new();
        };
        let direction = state.player.direction.clone();
        for (spot_x, spot_y) in aoe_targets(&target, &direction, tier) {
            let spot_tile = &mut state.world.scenes[index].tiles[spot_y as usize][spot_x as usize];
            if (spot_tile.background == tile_types::GRASS || spot_tile.background == tile_types::FLOOR)
                && spot_tile.node.is_none()
            {
                spot_tile.background = tile_types::SOIL.to_owned();
                spot_tile.r#type = tile_types::SOIL.to_owned();
                spot_tile.soil_moisture = 0;
                spot_tile.soil_fertility = 0;
                spot_tile.soil_state = Some(soil_states::DRY.to_owned());
            }
        }
        return finish(ctx, state, tool, energy_cost, vec![Effect::message(message_levels::SUCCESS, "Tilled soil!")]);
    }

    if tool_type == tool_types::SCYTHE {
        if let Some(crop) = &target.tile.crop {
            if crop.withered == Some(true) {
                let Some(index) = scene_index(state, &target.scene_id) else {
                    return Vec::new();
                };
                state.world.scenes[index].tiles[target.y as usize][target.x as usize].crop = None;
                return finish(
                    ctx,
                    state,
                    tool,
                    energy_cost,
                    vec![Effect::message(message_levels::SUCCESS, "Cleared the withered crop.")],
                );
            }
            if let Some(crop_def) = ctx.content.crops.get(&crop.r#type) {
                if crops::is_crop_mature_by_days(crop, crop_def) {
                    let effects = harvest_crop(ctx, state, &target.scene_id, target.x, target.y);
                    return finish(ctx, state, tool, energy_cost, effects);
                }
            }
            return vec![Effect::message(message_levels::INFO, "Crop is not ready to harvest yet")];
        }
    }

    if tool_type == tool_types::FISHING_ROD && target.tile.background == tile_types::WATER {
        // A declared 'fishing' minigame gates the catch on player skill; the
        // score re-enters through the resolveMinigame command. Without one the
        // cast resolves instantly (original behavior).
        let minigame = ctx.content.minigames.iter().find(|def| def.id == FISHING_MINIGAME_ID);
        if let Some(minigame) = minigame {
            if state.minigame.is_none() {
                let mut context: IndexMap<String, Value> = IndexMap::new();
                context.insert("builtin".to_owned(), Value::String("fishing".to_owned()));
                context.insert("rodTier".to_owned(), Value::from(tier));
                let effects = events::start_minigame_session(ctx, state, &minigame.id, Some(&context));
                return finish(ctx, state, tool, energy_cost, effects);
            }
        }
        let result = fishing::resolve_fishing(ctx, state, tier, None);
        return finish(ctx, state, tool, energy_cost, result.effects);
    }

    vec![Effect::message(message_levels::INFO, format!("Can't use {} here", tool_slot.item.name))]
}

pub fn handle_interact(ctx: &EngineContext, state: &mut GameState) -> Effects {
    let target = facing_tile(state);

    let facing = world_movement::facing_target(state);
    let target_x = facing.x;
    let target_y = facing.y;

    // Authored interact-events take priority over built-in interactions (M3).
    // JS `eventResult.state !== state` is a reference comparison; with in-place updates the
    // closest reading is "did the state change" (a fired event that leaves every value as it was
    // is indistinguishable from no event here).
    let before = state.clone();
    let event_effects =
        events::evaluate_events(ctx, state, "interact", Some(EventPosition { x: target_x, y: target_y }));
    if *state != before || !event_effects.is_empty() {
        return event_effects;
    }

    // NPC dialogue next (uses live NPC positions from state)
    if let Some(npc_entry_id) = social::npc_on_tile(ctx, state, target_x, target_y) {
        let npc_def = ctx.content.npcs.iter().find(|npc| npc.id == npc_entry_id);
        if let Some(npc_def) = npc_def {
            if !npc_def.dialogue.is_empty() {
                ctx.emit(HookEvent::NpcInteract(NpcInteractHookPayload { npc_id: npc_def.id.clone() }));
                state.dialogue =
                    Some(DialogueState { npc_id: npc_def.id.clone(), dialogue_id: npc_def.dialogue[0].id.clone() });
                return quests::progress_quests(ctx, state, "talk", &npc_def.id, 1);
            }
        }
        return Vec::new();
    }

    let Some(target) = target else {
        return Vec::new();
    };

    // Animals (M4c): feed → collect → pet
    let animal = animals::animal_at(state, &state.player.scene_id, target.x, target.y).cloned();
    if let Some(animal) = animal {
        return animals::handle_animal_interaction(ctx, state, &animal);
    }

    // Machines (M4a): collect finished output (loading goes through machineLoad)
    if let Some(machine) = &target.tile.machine {
        if machine.output.as_ref().is_some_and(|output| !output.is_empty()) {
            return crafting::collect_machine_output(ctx, state, &target.scene_id, target.x, target.y);
        }
        if machine.processing.is_some() {
            return vec![Effect::message(message_levels::INFO, "Still working…")];
        }
        let machine_def = ctx.content.machine_types.iter().find(|def| def.id == machine.type_id);
        let name = machine_def.map_or("Machine", |def| def.name.as_str());
        return vec![Effect::message(message_levels::INFO, format!("{name} is idle — load a recipe."))];
    }

    // Mine (M4f): entrance descends (elevator checkpoint when unlocked);
    // interacting near the entry tile of a floor climbs out.
    let mine_config = &ctx.content.mine;
    if mine_config.enabled
        && state.player.scene_id == mine_config.entrance_scene_id.as_deref().unwrap_or("")
        && target.x == mine_config.entrance_x.unwrap_or(-1)
        && target.y == mine_config.entrance_y.unwrap_or(-1)
    {
        // floor(deepest / every) × every; JS divides by zero into NaN, and max(1, NaN) is NaN.
        let checkpoint = state
            .mine
            .deepest_floor
            .checked_div(mine_config.elevator_every)
            .map_or(0, |elevators| elevators * mine_config.elevator_every);
        return mines::descend_mine(ctx, state, checkpoint.max(1));
    }
    if mines::is_mine_scene(&state.player.scene_id) && target.x == 1 && target.y == 1 {
        return mines::exit_mine(ctx, state);
    }

    if let Some(crop) = &target.tile.crop {
        if crop.withered == Some(true) {
            return vec![Effect::message(message_levels::INFO, "This crop withered — clear it with a scythe.")];
        }
        if let Some(definition) = ctx.content.crops.get(&crop.r#type) {
            if !crops::is_crop_mature_by_days(crop, definition) {
                return vec![Effect::message(message_levels::INFO, "Crop is not ready to harvest yet")];
            }
        }
        return harvest_crop(ctx, state, &target.scene_id, target.x, target.y);
    }

    if target.tile.background == tile_types::SOIL && !gathering::is_node_active(&target.tile) {
        return plant_seed(ctx, state, &target.scene_id, target.x, target.y);
    }

    Vec::new()
}

fn harvest_crop(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> Effects {
    let Some(scene) = world_movement::find_scene(state, scene_id) else {
        return Vec::new();
    };
    let tile = &scene.tiles[y as usize][x as usize];
    let Some(crop) = tile.crop.clone() else {
        return Vec::new();
    };
    if crop.withered == Some(true) {
        return Vec::new();
    }

    let Some(definition) = ctx.content.crops.get(&crop.r#type) else {
        return Vec::new();
    };
    if !crops::is_crop_mature_by_days(&crop, definition) {
        return vec![Effect::message(message_levels::INFO, "Crop is not ready to harvest yet")];
    }

    let crop_item_id = format!("crop-{}", crop.r#type);
    let Some(crop_item) = ctx.content.items.iter().find(|item| item.id == crop_item_id) else {
        return Vec::new();
    };

    let mut rng = Rng::new(state.rng.clone());
    let mutation = crops::roll_mutation(Some(definition), &crop.quality, &mut rng);
    // Farming skill: +1 yield per 4 levels (M4g)
    let quantity = crops::roll_yield(Some(definition), &crop.quality, mutation.as_deref(), &mut rng)
        .saturating_add(skills::farming_yield_bonus(state));
    let estimated_value =
        crops::calculate_harvest_value(Some(definition), &crop.quality, mutation.as_deref(), quantity);

    let add_result =
        inventory::add_item(&state.player.inventory, crop_item, quantity, state.player.max_inventory_size, None);
    if !add_result.added {
        // Full inventory aborts the harvest; the rng draws are discarded.
        return vec![Effect::message(message_levels::ERROR, "Inventory is full!")];
    }

    let Some(index) = scene_index(state, scene_id) else {
        return Vec::new();
    };
    let tile = &mut state.world.scenes[index].tiles[y as usize][x as usize];
    if definition.can_regrow {
        let growth_days = crops::crop_growth_days(definition);
        let regrowth = crops::crop_regrowth_days(definition);
        let mut regrown = Crop {
            days_grown: Some(growth_days.saturating_sub(regrowth)),
            harvest_count: crop.harvest_count.saturating_add(1),
            watered: false,
            ..crop.clone()
        };
        regrown.stage = crops::compute_crop_stage(&regrown, definition);
        tile.crop = Some(regrown);
    } else {
        tile.crop = None;
    }

    let quality_text = if crop.quality == crop_qualities::IRIDIUM {
        " ⭐⭐⭐"
    } else if crop.quality == crop_qualities::GOLD {
        " ⭐⭐"
    } else if crop.quality == crop_qualities::SILVER {
        " ⭐"
    } else {
        ""
    };
    // JS toUpperCase on the ASCII mutation ids.
    let mutation_text = match &mutation {
        Some(mutation) if !mutation.is_empty() => format!(" ({}!)", mutation.to_uppercase()),
        _ => String::new(),
    };

    state.rng = rng.state;
    state.player.inventory = add_result.inventory;

    let mut effects: Effects = vec![
        Effect::message(
            message_levels::SUCCESS,
            format!(
                "Harvested {}x {}{}{} (worth ~${})",
                quantity,
                definition.name,
                quality_text,
                mutation_text,
                estimated_value.map_or_else(|| "NaN".to_owned(), |value| value.to_string())
            ),
        ),
        Effect::CropHarvested { crop_type: crop.r#type.clone(), quantity },
    ];
    ctx.emit(HookEvent::CropHarvest(CropHarvestHookPayload {
        crop_type: crop.r#type.clone(),
        quantity,
        quality: crop.quality.clone(),
    }));

    effects.extend(skills::grant_xp(ctx, state, "farming", 8));
    effects.extend(quests::progress_quests(ctx, state, "harvest", &crop.r#type, quantity));
    effects
}

fn plant_seed(ctx: &EngineContext, state: &mut GameState, scene_id: &str, x: i32, y: i32) -> Effects {
    let Some(scene) = world_movement::find_scene(state, scene_id) else {
        return Vec::new();
    };

    let seed_slot = state.player.inventory.iter().find(|slot| slot.item.r#type == item_types::SEED);
    let Some(crop_type) = seed_slot.and_then(|slot| slot.item.crop_type.as_deref()).filter(|crop| !crop.is_empty())
    else {
        return vec![Effect::message(message_levels::INFO, "No seeds in inventory")];
    };
    let seed_item_id = seed_slot.map(|slot| slot.item.id.clone()).unwrap_or_default();

    let Some(definition) = ctx.content.crops.get(crop_type) else {
        return vec![Effect::message(message_levels::ERROR, "Invalid crop type!")];
    };

    if !crops::can_grow_in_season(Some(definition), &state.clock.season) {
        return vec![Effect::message(
            message_levels::ERROR,
            format!("{} cannot grow in {}!", definition.name, state.clock.season),
        )];
    }

    if let Some(multi_tile) = &definition.multi_tile {
        let can_place = crops::can_place_multi_tile_crop(&scene.tiles, x, y, multi_tile.width, multi_tile.height);
        if !can_place {
            return vec![Effect::message(message_levels::ERROR, "Not enough space for this crop!")];
        }
    }

    let crop_type = crop_type.to_owned();
    let mut inventory = inventory::remove_item(&state.player.inventory, &seed_item_id, 1);
    let fertilizer_item_id =
        inventory.iter().find(|slot| slot.item.r#type == item_types::FERTILIZER).map(|slot| slot.item.id.clone());
    let used_fertilizer = fertilizer_item_id.is_some();
    if let Some(fertilizer_item_id) = fertilizer_item_id {
        inventory = inventory::remove_item(&inventory, &fertilizer_item_id, 1);
    }

    let Some(index) = scene_index(state, scene_id) else {
        return Vec::new();
    };

    let new_crop = crops::create_planted_crop(&crop_type, state.clock.day, used_fertilizer);
    let multi_tile_id = format!("{}-{}-{}-{}", crop_type, state.clock.tick, x, y);
    let tiles = &mut state.world.scenes[index].tiles;

    if let Some(multi_tile) = &definition.multi_tile {
        for crop_dy in 0..multi_tile.height {
            for crop_dx in 0..multi_tile.width {
                // The placement check above kept every covered tile inside the scene.
                let ty = y as usize + crop_dy as usize;
                let tx = x as usize + crop_dx as usize;
                tiles[ty][tx].crop = Some(Crop {
                    is_multi_tile_root: Some(crop_dy == 0 && crop_dx == 0),
                    multi_tile_id: Some(multi_tile_id.clone()),
                    ..new_crop.clone()
                });
            }
        }
    } else {
        tiles[y as usize][x as usize].crop = Some(new_crop);
    }

    if used_fertilizer {
        let tile = &mut tiles[y as usize][x as usize];
        tile.soil_fertility = 100;
        tile.soil_state = Some(soil_states::FERTILIZED.to_owned());
    }

    state.player.inventory = inventory;

    vec![Effect::message(
        message_levels::SUCCESS,
        format!(
            "Planted {}!{} Water it so it grows.",
            definition.name,
            if used_fertilizer { " (Fertilized)" } else { "" }
        ),
    )]
}

/// Shared fixtures for the ported engine-level tests (`EngineTests.MakeProject` and the
/// `CoreTestHelpers` used by `M2SystemsTests`).
#[cfg(test)]
pub(crate) mod test_support {
    use crate::content_builtin;
    use crate::effects::Effect;
    use crate::engine_types::EngineContext;
    use crate::schema::{
        default_project_settings, Dialogue, DialogueOption, GameProject, GameState, InventorySlot, Item, MineConfig,
        Npc, Player, Quest, QuestObjective, QuestRewards, ShopDefinition, ShopStockEntry, WeatherConfig,
        WeatherTableEntry, WeatherTypeDefinition,
    };
    use crate::state;
    use crate::units;
    use crate::world::tiles;
    use indexmap::IndexMap;

    /// TS `effects.some(e => e.type === 'message' && predicate(e.text))`.
    pub(crate) fn has_message(effects: &[Effect], predicate: impl Fn(&str) -> bool) -> bool {
        effects.iter().any(|effect| matches!(effect, Effect::Message { text, .. } if predicate(text)))
    }

    pub(crate) fn slot(items: &[Item], id: &str, quantity: u32) -> InventorySlot {
        let item = items.iter().find(|item| item.id == id).unwrap_or_else(|| panic!("item {id}")).clone();
        InventorySlot { item, quantity }
    }

    /// Minimal test project: 6x6 open field with soil at (3,2), npc at (1,1).
    pub(crate) fn make_project() -> GameProject {
        let mut scene = tiles::create_empty_scene("scene-test", "Test Farm", 6, 6);
        scene.tiles[2][3] = tiles::set_tile_layer(&scene.tiles[2][3], "soil", None);
        scene.tiles[4][4] = tiles::set_tile_layer(&scene.tiles[4][4], "wall", None);

        let items = content_builtin::create_default_items();
        let npc = Npc {
            id: "npc-test".to_owned(),
            name: "Testy".to_owned(),
            x: units::tiles(1),
            y: units::tiles(1),
            scene_id: "scene-test".to_owned(),
            dialogue: vec![
                Dialogue {
                    id: "dlg-1".to_owned(),
                    npc_id: "npc-test".to_owned(),
                    text: "Hello!".to_owned(),
                    options: vec![
                        DialogueOption { text: "Bye".to_owned(), ..DialogueOption::default() },
                        DialogueOption {
                            text: "Gift me".to_owned(),
                            give_money: Some(25),
                            next_dialogue_id: Some("dlg-2".to_owned()),
                            ..DialogueOption::default()
                        },
                    ],
                    ..Dialogue::default()
                },
                Dialogue {
                    id: "dlg-2".to_owned(),
                    npc_id: "npc-test".to_owned(),
                    text: "More?".to_owned(),
                    options: vec![DialogueOption { text: "No".to_owned(), ..DialogueOption::default() }],
                    ..Dialogue::default()
                },
            ],
            can_move: false,
            appearance: "farmer".to_owned(),
            ..Npc::default()
        };

        let quest = Quest {
            id: "quest-wheat".to_owned(),
            name: "Wheat!".to_owned(),
            description: "Harvest 1 wheat".to_owned(),
            status: "active".to_owned(),
            objectives: vec![QuestObjective {
                id: "obj-1".to_owned(),
                r#type: "harvest".to_owned(),
                description: "Harvest wheat".to_owned(),
                target_crop_type: Some("wheat".to_owned()),
                target_crop_quantity: Some(1),
                completed: false,
                progress: 0,
                ..QuestObjective::default()
            }],
            rewards: QuestRewards { money: Some(100), ..QuestRewards::default() },
            ..Quest::default()
        };

        let sun_only = || vec![WeatherTableEntry { weather_id: "sun".to_owned(), weight: 1 }];
        let mut weather_table = IndexMap::new();
        for season in ["spring", "summer", "fall", "winter"] {
            weather_table.insert(season.to_owned(), sun_only());
        }

        GameProject {
            schema_version: 4,
            id: "proj-test".to_owned(),
            name: "Test".to_owned(),
            version: "2".to_owned(),
            scenes: vec![scene],
            npcs: vec![npc.clone()],
            items: items.clone(),
            events: Vec::new(),
            // Same list as the NPC's dialogue (TS `dialogues: npc.dialogue`).
            dialogues: npc.dialogue.clone(),
            quests: vec![quest],
            player: Player {
                x: units::tiles(3),
                y: units::tiles(4),
                direction: "up".to_owned(),
                scene_id: "scene-test".to_owned(),
                inventory: vec![
                    slot(&items, "seed-wheat", 5),
                    slot(&items, "tool-hoe", 1),
                    slot(&items, "tool-watering-can", 1),
                ],
                max_inventory_size: 10,
                money: 100,
                active_quests: vec!["quest-wheat".to_owned()],
                completed_quests: Vec::new(),
                pixel_x: 0.0,
                pixel_y: 0.0,
                target_x: 0.0,
                target_y: 0.0,
                ..Player::default()
            },
            event_flags: IndexMap::new(),
            start_scene_id: "scene-test".to_owned(),
            mode: "play".to_owned(),
            selected_tile_type: "grass".to_owned(),
            selected_npc_id: None,
            selected_item_id: None,
            current_time: 1_000_000.0,
            custom_assets: Vec::new(),
            current_season: "spring".to_owned(),
            current_day: 1,
            current_time_minutes: 6 * 60,
            current_year: 1,
            shops: Vec::new(),
            node_types: Vec::new(),
            settings: default_project_settings(),
            recipes: Vec::new(),
            actions: Vec::new(),
            minigames: Vec::new(),
            machine_types: Vec::new(),
            // Sun-only table keeps sim tests weather-independent (weather has its
            // own dedicated tests).
            weather: WeatherConfig {
                types: vec![WeatherTypeDefinition {
                    id: "sun".to_owned(),
                    name: "Sunny".to_owned(),
                    ..WeatherTypeDefinition::default()
                }],
                table: weather_table,
                ..WeatherConfig::default()
            },
            animal_species: Vec::new(),
            animals: Vec::new(),
            fish_tables: Vec::new(),
            mine: MineConfig { enabled: false, ..MineConfig::default() },
            content_packs: Vec::new(),
            game_start_time: 1_000_000.0,
            ..GameProject::default()
        }
    }

    pub(crate) fn make_engine_with(seed: &str, project: GameProject) -> (EngineContext, GameState) {
        let ctx = EngineContext::new(state::create_content_from_project(&project));
        let state = state::create_game_state(&project, Some(seed));
        (ctx, state)
    }

    /// `M2SystemsTests.MakeEngine`: the test project plus a test shop, seed "m2".
    pub(crate) fn make_m2_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
        let mut project = make_project();
        project.shops = vec![ShopDefinition {
            id: "shop-test".to_owned(),
            name: "Test Shop".to_owned(),
            stock: vec![
                ShopStockEntry { item_id: "seed-wheat".to_owned(), ..ShopStockEntry::default() },
                ShopStockEntry {
                    item_id: "seed-tomato".to_owned(),
                    seasons: Some(vec!["summer".to_owned()]),
                    ..ShopStockEntry::default()
                },
                ShopStockEntry {
                    item_id: "fertilizer-quality".to_owned(),
                    daily_limit: Some(2),
                    ..ShopStockEntry::default()
                },
                ShopStockEntry { item_id: "seed-carrot".to_owned(), price: Some(3), ..ShopStockEntry::default() },
            ],
            sell_price_multiplier: 1,
            buys_items: true,
            repairs_tools: true,
            repair_cost_per_point: 500,
            ..ShopDefinition::default()
        }];
        mutate(&mut project);
        make_engine_with("m2", project)
    }

    /// `state with { Player = state.Player with { X = 3, Y = 3, Direction = "up" } }`.
    pub(crate) fn facing_soil(state: &mut GameState) {
        state.player.x = units::tiles(3);
        state.player.y = units::tiles(3);
        state.player.direction = "up".to_owned();
    }
}

/// Port of engine.test.ts (`EngineTests.cs`): the tools and planting/harvesting cases.
#[cfg(test)]
mod engine_tests {
    use super::test_support::{facing_soil, has_message, make_engine_with, make_project};
    use crate::commands::Command;
    use crate::effects::Effect;
    use crate::engine::apply_command;
    use crate::engine_types::EngineContext;
    use crate::schema::GameState;
    use crate::units;

    const NEEDS_MODULES: &str = "needs Tiles/WorldMovement/Engine/Economy/GameTime/… ports — enable at integration";

    fn make_engine() -> (EngineContext, GameState) {
        make_engine_with("engine-test", make_project())
    }

    fn use_tool(tool: &str) -> Command {
        Command::UseTool { tool: tool.to_owned() }
    }

    /// Water the facing tile then sleep — one full watered day for a crop.
    fn water_and_sleep(ctx: &EngineContext, state: &mut GameState, days: usize) {
        for _ in 0..days {
            apply_command(ctx, state, &use_tool("watering-can"));
            apply_command(ctx, state, &Command::Sleep);
        }
    }

    // --- tools ---

    #[test]
    fn tills_grass_into_soil_consuming_durability_and_energy() {
        let _ = NEEDS_MODULES;
        let (ctx, mut state) = make_engine();
        // player at (3,4) facing up → target (3,3) is grass
        let effects = apply_command(&ctx, &mut state, &use_tool("hoe"));
        let tile = &state.world.scenes[0].tiles[3][3];
        assert_eq!(tile.background, "soil");
        assert_eq!(tile.soil_state.as_deref(), Some("dry"));
        assert!(effects.contains(&Effect::message("success", "Tilled soil!")));
        let hoe = state.player.inventory.iter().find(|s| s.item.tool_type.as_deref() == Some("hoe")).unwrap();
        assert_eq!(hoe.item.durability, Some(99));
        assert_eq!(state.player.energy, units::points(96)); // hoe costs 4
    }

    #[test]
    fn waters_soil_and_marks_crops_watered() {
        let (ctx, mut state) = make_engine();
        facing_soil(&mut state);
        apply_command(&ctx, &mut state, &use_tool("watering-can"));
        let tile = &state.world.scenes[0].tiles[2][3];
        assert_eq!(tile.soil_moisture, 100);
        assert_eq!(tile.soil_state.as_deref(), Some("watered"));
        assert_eq!(state.player.energy, units::points(98)); // watering can costs 2
    }

    #[test]
    fn reports_missing_tools() {
        let (ctx, mut state) = make_engine();
        state.player.inventory = Vec::new();
        let before = state.clone();
        let effects = apply_command(&ctx, &mut state, &use_tool("watering-can"));
        assert!(effects.contains(&Effect::message("error", "You need a watering can!")));
        assert_eq!(state, before);
    }

    // --- planting and harvesting (day-based) ---

    #[test]
    fn plants_a_seed_on_soil_consuming_it_crop_starts_unwatered() {
        let (ctx, mut state) = make_engine();
        facing_soil(&mut state);
        apply_command(&ctx, &mut state, &Command::Interact);
        let tile = &state.world.scenes[0].tiles[2][3];
        let crop = tile.crop.as_ref().unwrap();
        assert_eq!(crop.r#type, "wheat");
        assert!(!crop.watered);
        assert_eq!(crop.days_grown, Some(0));
        assert_eq!(crop.planted_on_day, Some(1));
        let seeds = state.player.inventory.iter().find(|s| s.item.id == "seed-wheat").unwrap();
        assert_eq!(seeds.quantity, 4);
    }

    #[test]
    fn refuses_out_of_season_crops() {
        let (ctx, mut state) = make_engine();
        state.clock.season = "summer".to_owned();
        facing_soil(&mut state);
        let effects = apply_command(&ctx, &mut state, &Command::Interact);
        assert!(state.world.scenes[0].tiles[2][3].crop.is_none());
        assert!(has_message(&effects, |t| t.contains("cannot grow in summer")));
    }

    #[test]
    fn crops_mature_after_growth_days_watered_days_harvest_yields_items_but_no_auto_money() {
        let (ctx, mut state) = make_engine();
        facing_soil(&mut state);
        apply_command(&ctx, &mut state, &Command::Interact); // plant wheat (3 growth days)
        water_and_sleep(&ctx, &mut state, 3);

        assert_eq!(state.world.scenes[0].tiles[2][3].crop.as_ref().and_then(|c| c.days_grown), Some(3));
        assert_eq!(state.clock.day, 4);

        let money_before_harvest = state.player.money;
        let effects = apply_command(&ctx, &mut state, &Command::Interact);
        assert!(state.world.scenes[0].tiles[2][3].crop.is_none());
        let wheat = state.player.inventory.iter().find(|s| s.item.id == "crop-wheat").expect("harvested wheat");
        assert!(wheat.quantity >= 1);
        // No auto-sell: money only moves via the quest reward (100).
        assert_eq!(state.player.money, money_before_harvest + 100);
        assert!(state.player.completed_quests.iter().any(|q| q == "quest-wheat"));
        assert_eq!(state.quests["quest-wheat"].status, "completed");
        assert!(effects.iter().any(|e| matches!(e, Effect::QuestCompleted { .. })));
    }

    #[test]
    fn unwatered_crops_do_not_grow_overnight() {
        let (ctx, mut state) = make_engine();
        facing_soil(&mut state);
        apply_command(&ctx, &mut state, &Command::Interact);
        apply_command(&ctx, &mut state, &Command::Sleep); // no watering
        let crop = state.world.scenes[0].tiles[2][3].crop.as_ref().unwrap();
        assert_eq!(crop.days_grown, Some(0));
        assert_eq!(crop.days_without_water, 1);
    }

    #[test]
    fn is_not_harvestable_before_maturity() {
        let (ctx, mut state) = make_engine();
        facing_soil(&mut state);
        apply_command(&ctx, &mut state, &Command::Interact);
        let effects = apply_command(&ctx, &mut state, &Command::Interact);
        assert!(effects.contains(&Effect::message("info", "Crop is not ready to harvest yet")));
        assert!(state.world.scenes[0].tiles[2][3].crop.is_some());
    }
}

/// Port of m2-systems.test.ts (`M2SystemsTests.cs`): the nightly regrowth case.
#[cfg(test)]
mod m2_systems_tests {
    use super::test_support::{make_m2_engine, slot};
    use crate::commands::Command;
    use crate::engine::apply_command;
    use crate::units;

    #[test]
    fn regrowing_crops_reset_to_a_partial_growth_state_on_harvest() {
        let (ctx, mut state) = make_m2_engine(|project| {
            let items = project.items.clone();
            project.player.inventory = vec![slot(&items, "seed-tomato", 1), slot(&items, "tool-watering-can", 1)];
        });
        // Summer for tomatoes (4 growth days, 2 regrowth days). Day 29 keeps
        // the season stable across the sleeps below.
        state.clock.season = "summer".to_owned();
        state.clock.day = 29;
        state.player.x = units::tiles(3);
        state.player.y = units::tiles(3);
        state.player.direction = "up".to_owned();
        apply_command(&ctx, &mut state, &Command::Interact);
        for _ in 0..4 {
            apply_command(&ctx, &mut state, &Command::UseTool { tool: "watering-can".to_owned() });
            apply_command(&ctx, &mut state, &Command::Sleep);
        }
        apply_command(&ctx, &mut state, &Command::Interact);
        let crop = state.world.scenes[0].tiles[2][3].crop.as_ref().expect("regrown crop");
        assert_eq!(crop.harvest_count, 1);
        assert_eq!(crop.days_grown, Some(2)); // growthDays 4 - regrowthDays 2
    }
}
