//! The social and dialogue cases of `M3SystemsTests.cs` / `M4SystemsTests.cs`
//! (m3-systems.test.ts, m4-systems.test.ts), driven through the `social` and `dialogue_system`
//! handlers directly. States come from the golden starter farm: npc-farmer at (3, 6) owns
//! `dialogue-farmer-greeting` (2 options) and `dialogue-farmer-crops`.

mod fixture_project;

use farm_sim::dialogue_system::{find_dialogue, handle_choose_dialogue_option, handle_close_dialogue};
use farm_sim::effects::Effect;
use farm_sim::schema::{
    Dialogue, DialogueOption, DialogueState, GameProject, GameState, GiftTastes, Npc, NpcSocialState, Quest,
    QuestObjective, QuestRewards, ShopSession,
};
use farm_sim::social::{friendship_with, gift_reaction, handle_give_gift, hearts, visible_dialogue_options};
use farm_sim::EngineContext;
use fixture_project::{at, give, has_message, make_engine, quantity};

const NPC: &str = "npc-farmer";
const GREETING: &str = "dialogue-farmer-greeting";

fn in_dialogue(state: &mut GameState) {
    state.dialogue = Some(DialogueState { npc_id: NPC.to_owned(), dialogue_id: GREETING.to_owned() });
}

/// Appends an option to the farmer's greeting, both on the NPC and in the global list (the C#
/// test project shares one list between them).
fn add_greeting_option(project: &mut GameProject, option: DialogueOption) {
    project.npcs[0].dialogue[0].options.push(option.clone());
    project.dialogues[0].options.push(option);
}

fn with_gift_tastes(project: &mut GameProject, tastes: GiftTastes) {
    project.npcs[0].gift_tastes = Some(tastes);
}

// --- quest-giver binding (M3) ---

#[test]
fn a_dialogue_option_can_offer_a_quest() {
    let (ctx, mut state) = make_engine("m3", |project| {
        project.quests.push(Quest {
            id: "quest-offered".to_owned(),
            name: "Offered".to_owned(),
            description: "From dialogue".to_owned(),
            status: "not-started".to_owned(),
            objectives: vec![QuestObjective {
                id: "o".to_owned(),
                r#type: "collect".to_owned(),
                description: "x".to_owned(),
                target_item_id: Some("material-wood".to_owned()),
                target_item_quantity: Some(2.0),
                ..QuestObjective::default()
            }],
            rewards: QuestRewards::default(),
            ..Quest::default()
        });
        add_greeting_option(
            project,
            DialogueOption {
                text: "Any work?".to_owned(),
                offer_quest_id: Some("quest-offered".to_owned()),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    let effects = handle_choose_dialogue_option(&ctx, &mut state, 2.0);
    assert!(state.player.active_quests.iter().any(|id| id == "quest-offered"));
    assert!(has_message(&effects, |t| t.contains("New quest")));
}

// --- social (M4d) ---

#[test]
fn gifting_applies_taste_deltas_and_daily_limits() {
    let (ctx, mut state) = make_engine("m4", |project| {
        with_gift_tastes(
            project,
            GiftTastes {
                loved: vec!["gift-flower".to_owned()],
                hated: vec!["junk-boot".to_owned()],
                ..GiftTastes::default()
            },
        );
    });
    give(&ctx, &mut state, "gift-flower", 2.0);
    // npc-farmer stands at (3, 6): face it from (3, 7).
    at(&mut state, 3.0, 7.0, "up");

    let effects = handle_give_gift(&ctx, &mut state, "gift-flower");
    assert_eq!(state.social[NPC].friendship, 80.0);
    assert_eq!(quantity(&state, "gift-flower"), Some(1.0));
    assert!(has_message(&effects, |t| t == "Old Farmer: They love it! (+80)"));

    // Second gift the same day is refused.
    let effects = handle_give_gift(&ctx, &mut state, "gift-flower");
    assert_eq!(state.social[NPC].friendship, 80.0);
    assert!(has_message(&effects, |t| t.contains("already received")));
}

#[test]
fn friendship_gates_dialogue_options_for_ui_and_engine_alike() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "Secret".to_owned(),
                requires_friendship: Some(100.0),
                give_money: Some(999.0),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    let dialogue = find_dialogue(&ctx, NPC, GREETING).expect("greeting exists").clone();
    assert_eq!(visible_dialogue_options(&ctx, &state, &dialogue).len(), 2);

    // Below the gate: index 2 does not exist among visible options → dialogue closes, no money.
    let mut blocked = state.clone();
    let effects = handle_choose_dialogue_option(&ctx, &mut blocked, 2.0);
    assert!(effects.is_empty());
    assert_eq!(blocked.player.money, 100.0);
    assert_eq!(blocked.dialogue, None);

    let mut friendly = state.clone();
    friendly.social.insert(NPC.to_owned(), NpcSocialState { friendship: 150.0, gifts_today: 0.0, last_gift_day: None });
    assert_eq!(visible_dialogue_options(&ctx, &friendly, &dialogue).len(), 3);
    let effects = handle_choose_dialogue_option(&ctx, &mut friendly, 2.0);
    assert_eq!(friendly.player.money, 1099.0);
    assert_eq!(effects, vec![Effect::message("success", "Received $999")]);
    assert_eq!(friendly.dialogue, None);
}

// --- social.ts helpers ---

#[test]
fn gift_reactions_follow_the_taste_tables_and_default_to_neutral() {
    let plain = Npc { id: "n".to_owned(), ..Npc::default() };
    assert_eq!(gift_reaction(&plain, "gift-flower"), "neutral");
    let picky = Npc {
        gift_tastes: Some(GiftTastes {
            loved: vec!["a".to_owned()],
            liked: vec!["b".to_owned()],
            disliked: vec!["c".to_owned()],
            hated: vec!["d".to_owned()],
            ..GiftTastes::default()
        }),
        ..plain
    };
    assert_eq!(gift_reaction(&picky, "a"), "loved");
    assert_eq!(gift_reaction(&picky, "b"), "liked");
    assert_eq!(gift_reaction(&picky, "c"), "disliked");
    assert_eq!(gift_reaction(&picky, "d"), "hated");
    assert_eq!(gift_reaction(&picky, "e"), "neutral");
}

#[test]
fn hearts_floor_friendship_per_125_points_and_missing_npcs_have_none() {
    let (_, mut state) = make_engine("m4", |_| {});
    assert_eq!(friendship_with(&state, NPC), 0.0);
    state.social.insert(NPC.to_owned(), NpcSocialState { friendship: 260.0, gifts_today: 0.0, last_gift_day: None });
    assert_eq!(friendship_with(&state, NPC), 260.0);
    assert_eq!(hearts(260.0), 2.0);
    assert_eq!(hearts(124.9), 0.0);
    assert_eq!(hearts(1250.0), 10.0);
}

#[test]
fn visible_dialogue_options_honor_required_items() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "Here is a flower".to_owned(),
                requires_item: Some("gift-flower".to_owned()),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    let dialogue = find_dialogue(&ctx, NPC, GREETING).expect("greeting exists").clone();
    assert_eq!(visible_dialogue_options(&ctx, &state, &dialogue).len(), 2);
    give(&ctx, &mut state, "gift-flower", 1.0);
    let visible = visible_dialogue_options(&ctx, &state, &dialogue);
    assert_eq!(visible.len(), 3);
    assert_eq!(visible[2].text, "Here is a flower");
    // The friendship gate only applies while a dialogue is open.
    state.dialogue = None;
    let mut gated = dialogue.clone();
    gated.options[0].requires_friendship = Some(500.0);
    assert_eq!(visible_dialogue_options(&ctx, &state, &gated).len(), 3);
}

#[test]
fn visible_dialogue_options_honor_required_flags() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "About that door…".to_owned(),
                requires_flag: Some("door-open".to_owned()),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    let dialogue = find_dialogue(&ctx, NPC, GREETING).expect("greeting exists").clone();
    assert_eq!(visible_dialogue_options(&ctx, &state, &dialogue).len(), 2);
    state.flags.insert("door-open".to_owned(), serde_json::Value::Bool(true));
    assert_eq!(visible_dialogue_options(&ctx, &state, &dialogue).len(), 3);
}

// --- dialogue.ts ---

#[test]
fn choosing_an_option_advances_to_the_next_dialogue_or_closes() {
    let (ctx, mut state) = make_engine("m4", |_| {});
    in_dialogue(&mut state);
    // Option 1 of the greeting leads to the crops dialogue.
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 1.0).is_empty());
    assert_eq!(
        state.dialogue,
        Some(DialogueState { npc_id: NPC.to_owned(), dialogue_id: "dialogue-farmer-crops".to_owned() })
    );
    // Its only option has no follow-up: the dialogue closes.
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 0.0).is_empty());
    assert_eq!(state.dialogue, None);
    // Without an open dialogue the command is a no-op.
    let before = state.clone();
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 0.0).is_empty());
    assert_eq!(state, before);
}

#[test]
fn a_missing_next_dialogue_closes_the_conversation() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "Tell me more".to_owned(),
                next_dialogue_id: Some("dialogue-missing".to_owned()),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 2.0).is_empty());
    assert_eq!(state.dialogue, None);
}

#[test]
fn invalid_indices_and_unknown_dialogues_close_the_dialogue() {
    let (ctx, state) = make_engine("m4", |_| {});
    for index in [-1.0, 0.5, 2.0, 99.0, f64::NAN] {
        let mut current = state.clone();
        in_dialogue(&mut current);
        assert!(handle_choose_dialogue_option(&ctx, &mut current, index).is_empty(), "index {index}");
        assert_eq!(current.dialogue, None, "index {index}");
        assert_eq!(current.player, state.player);
    }
    let mut unknown = state.clone();
    unknown.dialogue = Some(DialogueState { npc_id: NPC.to_owned(), dialogue_id: "dialogue-nope".to_owned() });
    assert!(handle_choose_dialogue_option(&ctx, &mut unknown, 0.0).is_empty());
    assert_eq!(unknown.dialogue, None);
}

#[test]
fn choosing_an_option_gives_money_and_items() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "Gift me".to_owned(),
                give_money: Some(25.0),
                give_item: Some("gift-flower".to_owned()),
                give_item_quantity: Some(2.0),
                next_dialogue_id: Some("dialogue-farmer-crops".to_owned()),
                ..DialogueOption::default()
            },
        );
        add_greeting_option(
            project,
            DialogueOption {
                text: "One flower".to_owned(),
                give_item: Some("gift-flower".to_owned()),
                give_item_quantity: Some(0.0),
                give_money: Some(0.0),
                ..DialogueOption::default()
            },
        );
        add_greeting_option(
            project,
            DialogueOption {
                text: "Ghost".to_owned(),
                give_item: Some("item-missing".to_owned()),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    let effects = handle_choose_dialogue_option(&ctx, &mut state, 2.0);
    assert_eq!(
        effects,
        vec![Effect::message("success", "Received $25"), Effect::message("success", "Received Flower x2")]
    );
    assert_eq!(state.player.money, 125.0);
    assert_eq!(quantity(&state, "gift-flower"), Some(2.0));
    assert_eq!(state.dialogue.as_ref().map(|d| d.dialogue_id.as_str()), Some("dialogue-farmer-crops"));

    // `giveItemQuantity || 1` and `giveMoney` of 0 is falsy.
    in_dialogue(&mut state);
    let effects = handle_choose_dialogue_option(&ctx, &mut state, 3.0);
    assert_eq!(effects, vec![Effect::message("success", "Received Flower")]);
    assert_eq!(quantity(&state, "gift-flower"), Some(3.0));
    assert_eq!(state.player.money, 125.0);
    assert_eq!(state.dialogue, None);

    // Unknown items give nothing, silently.
    in_dialogue(&mut state);
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 4.0).is_empty());

    // A full inventory reports it and keeps the money.
    in_dialogue(&mut state);
    state.player.inventory.retain(|s| s.item.id != "gift-flower");
    state.player.max_inventory_size = state.player.inventory.len() as f64;
    let effects = handle_choose_dialogue_option(&ctx, &mut state, 2.0);
    assert_eq!(
        effects,
        vec![Effect::message("success", "Received $25"), Effect::message("error", "Inventory is full!")]
    );
    assert_eq!(state.player.money, 150.0);
}

#[test]
fn a_shop_option_for_a_missing_shop_reports_an_error_and_still_closes_the_dialogue() {
    let (ctx, mut state) = make_engine("m4", |project| {
        add_greeting_option(
            project,
            DialogueOption {
                text: "Trade".to_owned(),
                open_shop_id: Some("shop-nope".to_owned()),
                give_money: Some(5.0),
                ..DialogueOption::default()
            },
        );
    });
    in_dialogue(&mut state);
    state.shop = Some(ShopSession { shop_id: "shop-general".to_owned() });
    let effects = handle_choose_dialogue_option(&ctx, &mut state, 2.0);
    assert_eq!(
        effects,
        vec![Effect::message("success", "Received $5"), Effect::message("error", "That shop does not exist.")]
    );
    assert_eq!(state.dialogue, None);
    // The existing session is kept.
    assert_eq!(state.shop, Some(ShopSession { shop_id: "shop-general".to_owned() }));
}

#[test]
fn the_merchant_greeting_opens_the_general_store() {
    let (ctx, mut state) = make_engine("m4", |_| {});
    state.dialogue =
        Some(DialogueState { npc_id: "npc-merchant".to_owned(), dialogue_id: "dialogue-merchant-greeting".to_owned() });
    assert!(handle_choose_dialogue_option(&ctx, &mut state, 0.0).is_empty());
    assert_eq!(state.shop, Some(ShopSession { shop_id: "shop-general".to_owned() }));
    assert_eq!(state.dialogue, None);
}

#[test]
fn close_dialogue_only_changes_state_while_one_is_open() {
    let (_, mut state) = make_engine("m4", |_| {});
    let before = state.clone();
    assert!(handle_close_dialogue(&mut state).is_empty());
    assert_eq!(state, before);
    in_dialogue(&mut state);
    assert!(handle_close_dialogue(&mut state).is_empty());
    assert_eq!(state.dialogue, None);
}

#[test]
fn find_dialogue_prefers_the_npc_owned_dialogue_over_the_global_list() {
    let (ctx, _) = make_engine("m4", |project| {
        project.dialogues.push(Dialogue {
            id: GREETING.to_owned(),
            npc_id: NPC.to_owned(),
            text: "A global duplicate".to_owned(),
            ..Dialogue::default()
        });
        project.dialogues.push(Dialogue {
            id: "dialogue-global-only".to_owned(),
            npc_id: NPC.to_owned(),
            text: "Only in the global list".to_owned(),
            ..Dialogue::default()
        });
    });
    assert!(find_dialogue(&ctx, NPC, GREETING).expect("owned").text.starts_with("Welcome to the farm!"));
    assert_eq!(
        find_dialogue(&ctx, NPC, "dialogue-global-only").map(|d| d.text.as_str()),
        Some("Only in the global list")
    );
    // An unknown NPC still resolves global dialogues.
    assert_eq!(
        find_dialogue(&ctx, "npc-nobody", "dialogue-global-only").map(|d| d.id.as_str()),
        Some("dialogue-global-only")
    );
    assert_eq!(find_dialogue(&ctx, NPC, "dialogue-nope"), None);
    let _: &EngineContext = &ctx;
}
