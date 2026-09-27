//! Port of `tests/FarmEngine.Core.Tests/Core/QuestsEngineTests.cs` (itself a port of
//! tests/unit/quests.engine.test.ts): quest behavior against the ONE canonical quest engine
//! (`farm_sim::quests`): no double-start/double-complete, prerequisite gating, target-0
//! objectives, full-inventory reward messages. The src/lib/quests selectors are one-line filters,
//! reproduced inline.

mod common;

use common::{empty_scene, has_message, slot};
use farm_sim::schema::{
    default_weather_config, GameProject, GameState, InventorySlot, MineConfig, Player, ProjectSettings, Quest,
    QuestObjective, QuestRewardItem, QuestRewards,
};
use farm_sim::{content_builtin, quests, state, Effect, EngineContext};

fn make_quest(id: &str) -> Quest {
    Quest {
        id: id.to_owned(),
        name: id.to_owned(),
        description: "test quest".to_owned(),
        status: "not-started".to_owned(),
        objectives: vec![QuestObjective {
            id: "obj-1".to_owned(),
            r#type: "collect".to_owned(),
            description: "collect wood".to_owned(),
            target_item_id: Some("material-wood".to_owned()),
            target_item_quantity: Some(2.0),
            completed: false,
            progress: 0.0,
            ..QuestObjective::default()
        }],
        rewards: QuestRewards::default(),
        ..Quest::default()
    }
}

fn make_project(mutate: impl FnOnce(&mut GameProject)) -> GameProject {
    let mut project = GameProject {
        schema_version: 7.0,
        id: "quest-test".to_owned(),
        name: "Quest Test".to_owned(),
        version: "2.0".to_owned(),
        scenes: vec![empty_scene("farm", "Farm", 8.0, 8.0)],
        items: content_builtin::create_default_items(),
        player: Player {
            x: 4.0,
            y: 4.0,
            direction: "up".to_owned(),
            scene_id: "farm".to_owned(),
            inventory: vec![],
            max_inventory_size: 3.0,
            money: 100.0,
            ..Player::default()
        },
        start_scene_id: "farm".to_owned(),
        mode: "play".to_owned(),
        selected_tile_type: "grass".to_owned(),
        current_time: 1.0,
        current_season: "spring".to_owned(),
        current_day: 1.0,
        current_time_minutes: 6.0 * 60.0,
        current_year: 1.0,
        shops: vec![content_builtin::create_default_shop()],
        settings: ProjectSettings::default(),
        weather: default_weather_config(),
        mine: MineConfig { enabled: false, ..MineConfig::default() },
        game_start_time: 1.0,
        ..GameProject::default()
    };
    mutate(&mut project);
    project
}

fn make_engine(mutate: impl FnOnce(&mut GameProject)) -> (EngineContext, GameState) {
    let project = make_project(mutate);
    (
        EngineContext::new(state::create_content_from_project(&project)),
        state::create_game_state(&project, Some("quests")),
    )
}

fn with_quests(quests: Vec<Quest>) -> impl FnOnce(&mut GameProject) {
    move |p| p.quests.extend(quests)
}

fn activate(quest: Quest, inventory: Option<Vec<InventorySlot>>) -> impl FnOnce(&mut GameProject) {
    move |p| {
        p.player.active_quests.push(quest.id.clone());
        p.quests.push(Quest { status: "active".to_owned(), ..quest });
        if let Some(inventory) = inventory {
            p.player.inventory = inventory;
        }
    }
}

fn completed(state: &GameState, ids: &[&str]) -> GameState {
    let mut next = state.clone();
    next.player.completed_quests = ids.iter().map(|id| (*id).to_owned()).collect();
    next
}

fn objective_progress(state: &GameState, quest_id: &str, objective_id: &str) -> f64 {
    state.quests[quest_id].objectives.as_ref().expect("objectives")[objective_id].progress
}

// --- startQuestById ---

#[test]
fn activates_a_quest_and_announces_it() {
    let (ctx, mut state) = make_engine(with_quests(vec![make_quest("q1")]));
    let effects = quests::start_quest_by_id(&ctx, &mut state, "q1");
    assert!(state.player.active_quests.contains(&"q1".to_owned()));
    assert!(has_message(&effects, |t| t.contains("New quest")));
}

#[test]
fn does_not_start_an_already_active_or_already_completed_quest() {
    let (ctx, mut state) = make_engine(with_quests(vec![make_quest("q1")]));
    quests::start_quest_by_id(&ctx, &mut state, "q1");
    let active = state.clone();
    assert!(quests::start_quest_by_id(&ctx, &mut state, "q1").is_empty());
    assert_eq!(state, active);

    let mut done = completed(&active, &["q1"]);
    done.player.active_quests.clear();
    quests::start_quest_by_id(&ctx, &mut done, "q1");
    assert!(!done.player.active_quests.contains(&"q1".to_owned()));
}

#[test]
fn gates_on_prerequisites() {
    let gated = Quest { prerequisites: Some(vec!["q1".to_owned()]), ..make_quest("q2") };
    let (ctx, state) = make_engine(with_quests(vec![make_quest("q1"), gated]));
    let mut blocked = state.clone();
    quests::start_quest_by_id(&ctx, &mut blocked, "q2");
    assert!(!blocked.player.active_quests.contains(&"q2".to_owned()));

    let mut unlocked = completed(&state, &["q1"]);
    quests::start_quest_by_id(&ctx, &mut unlocked, "q2");
    assert!(unlocked.player.active_quests.contains(&"q2".to_owned()));
}

// --- progressQuests ---

#[test]
fn accumulates_and_clamps_progress_to_the_objective_target_then_auto_completes() {
    let quest = Quest { rewards: QuestRewards { money: Some(10.0), ..QuestRewards::default() }, ..make_quest("q1") };
    let (ctx, mut state) = make_engine(activate(quest, None));
    quests::progress_quests(&ctx, &mut state, "collect", "material-wood", 1.0);
    assert_eq!(objective_progress(&state, "q1", "obj-1"), 1.0);
    assert!(!state.player.completed_quests.contains(&"q1".to_owned()));

    let effects = quests::progress_quests(&ctx, &mut state, "collect", "material-wood", 5.0);
    assert!(state.player.completed_quests.contains(&"q1".to_owned()));
    assert!(!state.player.active_quests.contains(&"q1".to_owned()));
    assert_eq!(state.player.money, 110.0);
    assert!(effects.iter().any(|e| matches!(e, Effect::QuestCompleted { .. })));
}

#[test]
fn treats_an_authored_target_of_0_as_already_satisfied() {
    let quest = Quest {
        objectives: vec![QuestObjective {
            id: "o".to_owned(),
            r#type: "collect".to_owned(),
            description: "none".to_owned(),
            target_item_id: Some("material-wood".to_owned()),
            target_item_quantity: Some(0.0),
            completed: false,
            progress: 0.0,
            ..QuestObjective::default()
        }],
        ..make_quest("q0")
    };
    let (ctx, mut state) = make_engine(activate(quest, None));
    quests::progress_quests(&ctx, &mut state, "collect", "material-wood", 0.0);
    assert!(state.player.completed_quests.contains(&"q0".to_owned()));
}

#[test]
fn ignores_non_matching_kinds_and_target_ids() {
    let (ctx, state) = make_engine(activate(make_quest("q1"), None));
    let mut wrong_kind = state.clone();
    quests::progress_quests(&ctx, &mut wrong_kind, "harvest", "material-wood", 1.0);
    assert_eq!(objective_progress(&wrong_kind, "q1", "obj-1"), 0.0);
    let mut wrong_target = state.clone();
    quests::progress_quests(&ctx, &mut wrong_target, "collect", "material-stone", 1.0);
    assert_eq!(objective_progress(&wrong_target, "q1", "obj-1"), 0.0);
}

#[test]
fn announces_item_rewards_that_do_not_fit_instead_of_dropping_them_silently() {
    let items = content_builtin::create_default_items();
    let quest = Quest {
        rewards: QuestRewards {
            items: Some(vec![QuestRewardItem { item_id: "seed-wheat".to_owned(), quantity: 3.0 }]),
            ..QuestRewards::default()
        },
        ..make_quest("q1")
    };
    let inventory =
        vec![slot(&items, "tool-hoe", 1.0), slot(&items, "tool-axe", 1.0), slot(&items, "tool-pickaxe", 1.0)];
    let (ctx, mut state) = make_engine(activate(quest, Some(inventory)));
    let effects = quests::progress_quests(&ctx, &mut state, "collect", "material-wood", 2.0);
    assert!(state.player.completed_quests.contains(&"q1".to_owned()));
    assert!(effects
        .iter()
        .any(|e| matches!(e, Effect::Message { level, text } if level == "error" && text.contains("reward lost"))));
}

// --- completeQuestById ---

#[test]
fn only_completes_active_quests_and_only_once() {
    let (ctx, mut state) = make_engine(|p| {
        p.quests =
            vec![Quest { rewards: QuestRewards { money: Some(25.0), ..QuestRewards::default() }, ..make_quest("q1") }];
        p.player.active_quests = vec!["q1".to_owned()];
    });
    quests::complete_quest_by_id(&ctx, &mut state, "q1");
    assert_eq!(state.player.money, 125.0);
    assert_eq!(state.player.completed_quests, vec!["q1".to_owned()]);
    // Second completion is a no-op: not active any more.
    assert!(quests::complete_quest_by_id(&ctx, &mut state, "q1").is_empty());
    assert_eq!(state.player.money, 125.0);
    assert_eq!(state.player.completed_quests, vec!["q1".to_owned()]);
}

// --- autoStartQuests ---

#[test]
fn starts_only_auto_start_quests_whose_prerequisites_are_met() {
    let (ctx, mut state) = make_engine(with_quests(vec![
        Quest { auto_start: Some(true), ..make_quest("auto-ok") },
        Quest { auto_start: Some(true), prerequisites: Some(vec!["auto-ok".to_owned()]), ..make_quest("auto-gated") },
        make_quest("manual"),
    ]));
    quests::auto_start_quests(&ctx, &mut state);
    assert_eq!(state.player.active_quests, vec!["auto-ok".to_owned()]);
}

#[test]
fn leaves_the_state_untouched_when_nothing_starts() {
    let (ctx, mut state) = make_engine(|_| {});
    let before = state.clone();
    quests::auto_start_quests(&ctx, &mut state);
    assert_eq!(state, before);
}

// --- project-level quest selectors (src/lib/quests) ---

#[test]
fn selects_by_membership_in_the_player_lists() {
    let project = make_project(|p| {
        p.quests = vec![make_quest("a"), make_quest("b"), make_quest("c")];
        p.player.active_quests = vec!["a".to_owned()];
        p.player.completed_quests = vec!["c".to_owned()];
    });
    let active: Vec<&str> =
        project.quests.iter().filter(|q| project.player.active_quests.contains(&q.id)).map(|q| q.id.as_str()).collect();
    let done: Vec<&str> = project
        .quests
        .iter()
        .filter(|q| project.player.completed_quests.contains(&q.id))
        .map(|q| q.id.as_str())
        .collect();
    assert_eq!(active, vec!["a"]);
    assert_eq!(done, vec!["c"]);
}

/// The one deliberate difference from the C#: completing a quest that never had a progress
/// entry produces `{ status: 'completed' }` with no `objectives` key, as the TypeScript spread
/// `{ ...state.quests[questId], status: 'completed' }` does.
#[test]
fn completing_a_quest_without_a_progress_entry_writes_no_objectives_key() {
    let (ctx, mut state) = make_engine(|p| {
        p.quests = vec![make_quest("q1")];
        p.player.active_quests = vec!["q1".to_owned()];
    });
    state.quests.shift_remove("q1");
    quests::complete_quest_by_id(&ctx, &mut state, "q1");
    assert_eq!(state.quests["q1"].status, "completed");
    assert_eq!(state.quests["q1"].objectives, None);
    assert_eq!(farm_sim::stable_stringify(&state.quests), r#"{"q1":{"status":"completed"}}"#);
}
