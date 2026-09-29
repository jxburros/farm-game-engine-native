//! The fishing case of the retired C# `M4SystemsTests.cs` (m4-systems.test.ts)
//! plus direct coverage of `resolve_fishing`'s branches. The starter farm carries the built-in
//! pond table (carp/perch/catfish, 15% junk).

mod fixture_project;

use farm_sim::effects::Effect;
use farm_sim::fishing::{active_fish_table, resolve_fishing, FishingResult};
use farm_sim::rng::next_u32;
use farm_sim::schema::{FishTable, FishTableEntry, GameProject, GameState};
use farm_sim::units::{self, Probability};
use farm_sim::EngineContext;
use fixture_project::{at, give, make_engine, quantity};

/// C# `WithWaterRow`: water across y=0 of the first scene.
fn with_water_row(project: &mut GameProject) {
    for tile in project.scenes[0].tiles[0].iter_mut().take(6) {
        tile.r#type = "water".to_owned();
        tile.background = "water".to_owned();
    }
}

fn make_m4_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    make_engine("m4", mutate)
}

/// A one-entry table whose escape roll is decided by `difficulty`.
fn table(junk_chance: f64, entries: Vec<FishTableEntry>) -> FishTable {
    FishTable {
        id: "fish-table-test".to_owned(),
        name: "Test Pond".to_owned(),
        entries,
        junk_chance: units::from_authoring::<Probability>(junk_chance),
        junk_item_id: Some("junk-boot".to_owned()),
        ..FishTable::default()
    }
}

fn carp(difficulty: f64) -> FishTableEntry {
    FishTableEntry {
        item_id: "fish-carp".to_owned(),
        weight: 1,
        difficulty: units::from_authoring::<Probability>(difficulty),
    }
}

// --- fishing (M4e) ---

#[test]
fn casts_resolve_deterministically_through_the_seeded_rng() {
    let (ctx, mut state) = make_m4_engine(with_water_row);
    give(&ctx, &mut state, "tool-fishing-rod", 1);
    at(&mut state, 3, 1, "up");

    let mut a = state.clone();
    let mut b = state.clone();
    for _ in 0..10 {
        farm_sim::engine::apply_command(&ctx, &mut a, &farm_sim::Command::UseTool { tool: "fishing-rod".to_owned() });
        farm_sim::engine::apply_command(&ctx, &mut b, &farm_sim::Command::UseTool { tool: "fishing-rod".to_owned() });
    }
    assert_eq!(a.player.inventory, b.player.inventory);
    // Ten casts with the default table should land SOMETHING (fish or junk).
    let catch_count: u32 = a
        .player
        .inventory
        .iter()
        .filter(|s| s.item.r#type == "fish" || s.item.id == "junk-boot")
        .map(|s| s.quantity)
        .sum();
    assert!(catch_count > 0);
    assert!(a.player.energy < units::points(100)); // rod costs energy
}

// --- fishing.ts branches ---

#[test]
fn active_fish_table_filters_by_scene_season_and_entries() {
    let (ctx, state) = make_m4_engine(|_| {});
    assert_eq!(active_fish_table(&ctx, &state).map(|t| t.id.as_str()), Some("fish-table-default"));

    let (ctx, state) = make_m4_engine(|project| project.fish_tables[0].scene_ids = Some(vec!["elsewhere".to_owned()]));
    assert_eq!(active_fish_table(&ctx, &state), None);
    let (ctx, state) = make_m4_engine(|project| project.fish_tables[0].scene_ids = Some(vec!["scene-farm".to_owned()]));
    assert!(active_fish_table(&ctx, &state).is_some());

    let (ctx, state) = make_m4_engine(|project| project.fish_tables[0].seasons = Some(vec!["winter".to_owned()]));
    assert_eq!(active_fish_table(&ctx, &state), None);
    let (ctx, mut state) = make_m4_engine(|project| project.fish_tables[0].seasons = Some(vec!["winter".to_owned()]));
    state.clock.season = "winter".to_owned();
    assert!(active_fish_table(&ctx, &state).is_some());

    // Empty restriction lists do not restrict; a table without entries is skipped.
    let (ctx, state) = make_m4_engine(|project| {
        project.fish_tables[0].seasons = Some(Vec::new());
        project.fish_tables[0].scene_ids = Some(Vec::new());
    });
    assert!(active_fish_table(&ctx, &state).is_some());
    let (ctx, state) = make_m4_engine(|project| {
        project.fish_tables[0].entries.clear();
        project.fish_tables.push(table(0.0, vec![carp(0.5)]));
    });
    assert_eq!(active_fish_table(&ctx, &state).map(|t| t.id.as_str()), Some("fish-table-test"));
}

#[test]
fn quiet_water_without_a_table_leaves_the_state_untouched() {
    let (ctx, mut state) = make_m4_engine(|project| project.fish_tables.clear());
    let before = state.clone();
    let result = resolve_fishing(&ctx, &mut state, 1, None);
    assert_eq!(
        result,
        FishingResult {
            effects: vec![Effect::message("info", "The water is quiet — nothing seems to live here.")],
            caught: false,
        }
    );
    assert_eq!(state, before);
}

#[test]
fn junk_rolls_first_and_lands_in_the_inventory_deterministically() {
    let (ctx, state) = make_m4_engine(|project| project.fish_tables = vec![table(1.0, vec![carp(0.0)])]);
    let (_, expected_rng) = next_u32(&state.rng);

    let mut a = state.clone();
    let result = resolve_fishing(&ctx, &mut a, 1, None);
    assert_eq!(
        result,
        FishingResult { effects: vec![Effect::message("info", "You fished up Old Boot…")], caught: false }
    );
    assert_eq!(quantity(&a, "junk-boot"), Some(1));
    // Exactly one draw: the junk roll.
    assert_eq!(a.rng, expected_rng);

    let mut b = state.clone();
    resolve_fishing(&ctx, &mut b, 1, None);
    assert_eq!(a, b);

    // With the inventory full the junk is lost but the draw still happened.
    let mut full = state.clone();
    full.player.max_inventory_size = full.player.inventory.len() as u32;
    let result = resolve_fishing(&ctx, &mut full, 1, None);
    assert_eq!(result, FishingResult { effects: vec![Effect::message("error", "Inventory is full!")], caught: false });
    assert_eq!(full.rng, expected_rng);
    assert_eq!(quantity(&full, "junk-boot"), None);
}

#[test]
fn junk_without_an_item_id_still_spends_the_roll_then_fishes() {
    let (ctx, mut state) = make_m4_engine(|project| {
        let mut always_junk = table(1.0, vec![carp(1.0)]);
        always_junk.junk_item_id = None;
        project.fish_tables = vec![always_junk];
    });
    let (_, after_junk) = next_u32(&state.rng);
    let (_, after_weighted) = next_u32(&after_junk);
    let (_, after_escape) = next_u32(&after_weighted);
    let result = resolve_fishing(&ctx, &mut state, 1, None);
    // Difficulty 1 with a tier-1 rod: the fish always escapes.
    assert_eq!(result, FishingResult { effects: vec![Effect::message("info", "It got away!")], caught: false });
    assert_eq!(state.rng, after_escape);
    assert_eq!(quantity(&state, "junk-boot"), None);
    assert_eq!(quantity(&state, "fish-carp"), None);
}

#[test]
fn escaping_fish_report_it_got_away_after_the_weighted_and_escape_rolls() {
    let (ctx, state) = make_m4_engine(|project| project.fish_tables = vec![table(0.0, vec![carp(1.0)])]);
    let (_, after_weighted) = next_u32(&state.rng);
    let (_, after_escape) = next_u32(&after_weighted);

    let mut current = state.clone();
    let result = resolve_fishing(&ctx, &mut current, 1, None);
    assert_eq!(result, FishingResult { effects: vec![Effect::message("info", "It got away!")], caught: false });
    // No junk draw (junkChance 0), then the weighted pick and the escape check.
    assert_eq!(current.rng, after_escape);
    assert_eq!(current.player, state.player);

    // A minigame score of 0 does not help either.
    let mut scored = state.clone();
    let result = resolve_fishing(&ctx, &mut scored, 1, Some(0));
    assert_eq!(result.effects, vec![Effect::message("info", "It got away!")]);
    assert_eq!(scored.rng, after_escape);
}

#[test]
fn a_zero_weight_table_is_not_even_a_nibble() {
    let (ctx, mut state) = make_m4_engine(|project| {
        let mut entry = carp(0.0);
        entry.weight = 0;
        project.fish_tables = vec![table(0.0, vec![entry])];
    });
    let before = state.clone();
    let result = resolve_fishing(&ctx, &mut state, 1, None);
    assert_eq!(result, FishingResult { effects: vec![Effect::message("info", "Not even a nibble.")], caught: false });
    // `weighted` returns -1 without drawing for an all-zero table.
    assert_eq!(state, before);
}

#[test]
fn a_perfect_minigame_score_always_lands_the_fish() {
    let (ctx, mut state) = make_m4_engine(|project| project.fish_tables = vec![table(0.0, vec![carp(1.0)])]);
    let result = resolve_fishing(&ctx, &mut state, 1, Some(units::PROBABILITY_ONE));
    assert!(result.caught);
    assert_eq!(result.effects[0], Effect::message("success", "Caught a Carp!"));
    assert_eq!(quantity(&state, "fish-carp"), Some(1));
}

#[test]
fn higher_rod_tiers_reduce_the_escape_chance() {
    // Difficulty 0.15 minus 0.15 per tier above 1: a tier-2 rod never loses this carp.
    let (ctx, mut state) = make_m4_engine(|project| project.fish_tables = vec![table(0.0, vec![carp(0.15)])]);
    let result = resolve_fishing(&ctx, &mut state, 2, None);
    assert!(result.caught);
}
