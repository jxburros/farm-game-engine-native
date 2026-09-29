//! Property tests of the save formats (docs/LANGUAGES.md "Property tests"): saving any state a
//! game can reach and loading it again gives the same state, in both envelopes.

// The random-play strategies of farm-sim's property tests.
#[path = "../../farm-sim/tests/strategies/mod.rs"]
mod strategies;

use farm_cart::save_file::{load_save, load_save_bytes, write_save, write_save_binary, SavePreview, SaveTarget};
use farm_sim::schema::{GameContent, GameProject, GameState};
use farm_sim::{hash_state, replay, state, EngineContext};
use proptest::prelude::*;
use std::sync::LazyLock;
use strategies::Ids;

struct Fixture {
    project: GameProject,
    content: GameContent,
    target: SaveTarget,
    ids: Ids,
}

static STARTER: LazyLock<Fixture> = LazyLock::new(|| {
    let project = strategies::golden_project("starter-farm");
    let content = state::create_content_from_project(&project);
    let target = SaveTarget::for_project(&project, &content);
    let ids = Ids::of(&project);
    Fixture { project, content, target, ids }
});

/// A new game after `inputs`.
fn reached(fixture: &Fixture, seed: &str, inputs: &[replay::ReplayInput]) -> GameState {
    let ctx = EngineContext::new(fixture.content.clone());
    let mut game = state::create_game_state(&fixture.project, Some(seed));
    replay::run_replay(&ctx, &mut game, inputs);
    game
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 48, ..ProptestConfig::default() })]

    #[test]
    fn save_then_load_gives_the_same_state(
        seed in strategies::seed(),
        inputs in strategies::inputs(&STARTER.ids, 40, 400),
    ) {
        let fixture = &*STARTER;
        let game = reached(fixture, &seed, &inputs);
        let hash = hash_state(&game);

        let binary = write_save_binary(&game, &fixture.target, &SavePreview::of_state(&game));
        let loaded = load_save_bytes(&binary, &fixture.target, &fixture.content);
        prop_assert!(loaded.ok, "binary save refused: {:?}", loaded.errors);
        prop_assert!(!loaded.migrated && loaded.warnings.is_empty(), "binary save: {:?}", loaded.warnings);
        let from_binary = loaded.state.expect("a loaded save has a state");
        prop_assert_eq!(&hash, &hash_state(&from_binary));
        prop_assert!(from_binary == game, "the binary save changed the state");

        let json = write_save(&game, &fixture.target);
        let loaded = load_save(&json, &fixture.target, &fixture.content);
        prop_assert!(loaded.ok, "JSON save refused: {:?}", loaded.errors);
        let from_json = loaded.state.expect("a loaded save has a state");
        prop_assert_eq!(&hash, &hash_state(&from_json));
        prop_assert!(from_json == game, "the JSON save changed the state");

        // A loaded game plays on like the original: one more day gives the same hash.
        let ctx = EngineContext::new(fixture.content.clone());
        let (mut original, mut restored) = (game, from_binary);
        farm_sim::apply_command(&ctx, &mut original, &farm_sim::Command::Sleep);
        farm_sim::apply_command(&ctx, &mut restored, &farm_sim::Command::Sleep);
        prop_assert_eq!(hash_state(&original), hash_state(&restored));
    }
}
