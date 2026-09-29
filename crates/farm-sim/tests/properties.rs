//! Property tests of the determinism contract (docs/LANGUAGES.md "Property tests"): the same
//! seed and the same command log give the same state, whatever the commands.

mod strategies;

use farm_sim::replay::{self, ReplayInput};
use farm_sim::schema::{GameContent, GameProject};
use farm_sim::{state, EngineContext, HookBus, StepOutput};
use proptest::prelude::*;
use std::sync::LazyLock;
use strategies::Ids;

struct Fixture {
    project: GameProject,
    content: GameContent,
    ids: Ids,
}

/// The starter farm: every system on (crops, shop, quests, crafting, machines, animals, fishing).
static STARTER: LazyLock<Fixture> = LazyLock::new(|| {
    let project = strategies::golden_project("starter-farm");
    let content = state::create_content_from_project(&project);
    let ids = Ids::of(&project);
    Fixture { project, content, ids }
});

/// Plays `inputs` on a new game: the final state hash, and every effect and hook event.
fn play(fixture: &Fixture, seed: &str, inputs: &[ReplayInput]) -> (String, StepOutput) {
    let ctx = EngineContext::with_hooks(fixture.content.clone(), HookBus::new());
    let mut game = state::create_game_state(&fixture.project, Some(seed));
    let result = replay::run_replay(&ctx, &mut game, inputs);
    (result.hash, StepOutput { effects: result.effects, hook_events: ctx.drain_hook_events() })
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 64, ..ProptestConfig::default() })]

    #[test]
    fn replaying_the_same_log_from_the_same_seed_gives_the_same_hash(
        seed in strategies::seed(),
        inputs in strategies::inputs(&STARTER.ids, 40, 400),
    ) {
        let (hash, output) = play(&STARTER, &seed, &inputs);
        let (again, output_again) = play(&STARTER, &seed, &inputs);
        prop_assert_eq!(hash, again);
        prop_assert_eq!(output, output_again);
    }

    /// `advance_tick(n)` is exactly `n` × `advance_tick(1)`: how a frame's ticks are batched
    /// never changes the game.
    #[test]
    fn splitting_ticks_does_not_change_the_hash(
        seed in strategies::seed(),
        inputs in strategies::inputs(&STARTER.ids, 20, 400),
    ) {
        let split: Vec<ReplayInput> = inputs
            .iter()
            .flat_map(|input| match input {
                ReplayInput::Tick { ticks } if *ticks >= 2.0 => {
                    let first = (ticks / 2.0).floor();
                    vec![replay::ticks(first), replay::ticks(ticks - first)]
                }
                other => vec![other.clone()],
            })
            .collect();
        prop_assert_eq!(play(&STARTER, &seed, &inputs).0, play(&STARTER, &seed, &split).0);
    }
}
