//! Port of `Runtime/MinigamesTests.cs` (itself a port of minigames.test.ts, plus the timing-bar
//! model, which TS drives through the DOM).

use farm_runtime::minigames::{
    self, MinigameConfig, MinigameImpl, MinigameMountOptions, MinigameRegistry, MinigameSession, TimingBarSession,
};
use farm_sim::js;
use farm_sim::schema::MinigameDef;
use serde_json::Value;
use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

fn def(kind: &str) -> MinigameDef {
    MinigameDef { id: "x".to_owned(), name: "X".to_owned(), kind: kind.to_owned(), ..MinigameDef::default() }
}

/// C# `Assert.Equal(expected, actual, 9)`: equal after rounding both to 9 decimals.
fn assert_close(expected: f64, actual: f64) {
    assert!((expected - actual).abs() < 5e-10, "expected {expected}, got {actual}");
}

#[test]
fn ships_timing_bar_and_resolves_registered_kinds() {
    let registry = minigames::create_default_minigame_registry();
    assert!(Arc::ptr_eq(&minigames::timing_bar(), &registry.get("timing-bar").unwrap()));
    assert!(Arc::ptr_eq(&minigames::timing_bar(), &minigames::minigame_impl_for(&registry, Some(&def("timing-bar")))));
}

struct Custom;

impl MinigameImpl for Custom {
    fn mount(&self, _options: MinigameMountOptions) -> Box<dyn MinigameSession> {
        unimplemented!("never mounted")
    }
}

#[test]
fn lets_game_code_register_custom_kinds() {
    let mut registry = MinigameRegistry::new();
    let custom: Arc<dyn MinigameImpl> = Arc::new(Custom);
    registry.register("rhythm", Arc::clone(&custom));
    assert!(Arc::ptr_eq(&custom, &minigames::minigame_impl_for(&registry, Some(&def("rhythm")))));
}

#[test]
fn falls_back_to_the_neutral_implementation_for_unknown_kinds_and_missing_defs() {
    let registry = minigames::create_default_minigame_registry();
    assert!(Arc::ptr_eq(&minigames::fallback(), &minigames::minigame_impl_for(&registry, Some(&def("unregistered")))));
    assert!(Arc::ptr_eq(&minigames::fallback(), &minigames::minigame_impl_for(&registry, None)));
}

// --- host-agnostic session models ---

fn mount(
    implementation: &Arc<dyn MinigameImpl>,
    config: MinigameConfig,
    random: f64,
) -> (Box<dyn MinigameSession>, Rc<RefCell<Vec<f64>>>) {
    let scores = Rc::new(RefCell::new(Vec::new()));
    let sink = Rc::clone(&scores);
    let options = MinigameMountOptions::new(config, move |score| sink.borrow_mut().push(score), || {})
        .with_random(move || random);
    (implementation.mount(options), scores)
}

fn config(entries: &[(&str, Value)]) -> MinigameConfig {
    entries.iter().map(|(key, value)| ((*key).to_owned(), value.clone())).collect()
}

#[test]
fn timing_bar_scores_one_inside_the_zone_and_falls_off_linearly_outside() {
    // random 0.5 → target center 0.5; default speed 0.9 sweeps/s.
    let (mut session, scores) = mount(&minigames::timing_bar(), MinigameConfig::new(), 0.5);
    {
        let bar = session.as_any().downcast_ref::<TimingBarSession>().expect("a TimingBarSession");
        assert_eq!(bar.target_center(), 0.5);
        assert_eq!(bar.target_size(), 0.18);
        assert_eq!(bar.prompt(), "Stop the marker in the zone!");
    }

    session.update(0.5 / 0.9); // marker at 0.5
    assert_close(0.5, session.as_any().downcast_ref::<TimingBarSession>().unwrap().position());
    session.press();
    session.press(); // at most once
    assert_eq!(*scores.borrow(), [1.0]);
    assert!(session.is_done());
}

#[test]
fn timing_bar_miss_scores_by_distance_and_sweeps_back() {
    let (mut session, scores) = mount(
        &minigames::timing_bar(),
        config(&[("speed", js::value(1.0)), ("targetSize", js::value(0.1)), ("prompt", Value::from("Go"))]),
        0.5,
    );
    let bar = session.as_any_mut().downcast_mut::<TimingBarSession>().unwrap();
    assert_eq!(bar.prompt(), "Go");
    bar.update(1.8); // triangle wave: 1.8 → 0.2
    assert_close(0.2, bar.position());
    bar.stop();
    assert_eq!(scores.borrow().len(), 1);
    assert_close(0.7, scores.borrow()[0]);
}

#[test]
fn timing_bar_clamps_config() {
    let (session, _) = mount(
        &minigames::timing_bar(),
        config(&[("speed", js::value(0.0)), ("targetSize", js::value(5.0)), ("prompt", js::value(3.0))]),
        0.5,
    );
    let bar = session.as_any().downcast_ref::<TimingBarSession>().unwrap();
    assert_eq!(bar.speed(), 0.1);
    assert_eq!(bar.target_size(), 0.9);
    assert_eq!(bar.prompt(), "Stop the marker in the zone!");
}

#[test]
fn fallback_scores_a_neutral_half_once() {
    let (mut session, scores) = mount(&minigames::fallback(), MinigameConfig::new(), 0.5);
    assert_eq!(session.prompt(), "Ready?");
    assert_eq!(session.button_text(), "Go!");
    session.press();
    session.press();
    assert_eq!(*scores.borrow(), [0.5]);
}

#[test]
fn disposed_sessions_never_complete() {
    let (mut session, scores) = mount(&minigames::fallback(), MinigameConfig::new(), 0.5);
    session.dispose();
    session.press();
    assert!(scores.borrow().is_empty());
}

#[test]
fn cancel_reports_once_without_a_score() {
    let cancelled = Rc::new(RefCell::new(0));
    let scores = Rc::new(RefCell::new(Vec::<f64>::new()));
    let (cancel_sink, score_sink) = (Rc::clone(&cancelled), Rc::clone(&scores));
    let mut session = minigames::timing_bar().mount(MinigameMountOptions::new(
        MinigameConfig::new(),
        move |score| score_sink.borrow_mut().push(score),
        move || *cancel_sink.borrow_mut() += 1,
    ));
    session.cancel();
    session.cancel();
    session.press();
    assert_eq!(*cancelled.borrow(), 1);
    assert!(scores.borrow().is_empty());
}
