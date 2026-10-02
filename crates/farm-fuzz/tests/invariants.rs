//! The state invariants (`farm_fuzz::invariants`) under generated content (#116): a golden
//! project with random settings, player, items, shops and scene sizes, played with a random
//! command log, checked after every command and tick. Content edits go past what the editor
//! allows: hosts take project JSON from anywhere.
//!
//! A failure prints the shrunk project edits and log bytes; `PROPTEST_RNG_SEED` replays a run.

use farm_fuzz::input::{self, Choices, Ids};
use farm_sim::schema::{GameProject, InventorySlot};
use farm_sim::{state, units, CommandRules, EngineContext, HookBus};
use proptest::prelude::*;
use serde_json::Value;
use std::path::PathBuf;
use std::sync::LazyLock;

const BASES: [&str; 6] = ["starter-farm", "cozy-garden", "quest-rpg", "lab-calendar", "blank", "fixture-v8"];

static PROJECTS: LazyLock<Vec<GameProject>> = LazyLock::new(|| {
    BASES
        .iter()
        .map(|name| {
            let path: PathBuf =
                [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "content", &format!("{name}.json")]
                    .iter()
                    .collect();
            let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
            let fixture: Value = serde_json::from_str(&text).expect("fixture JSON");
            serde_json::from_value(fixture["project"].clone()).expect("a project")
        })
        .collect()
});

/// One change to a project.
#[derive(Debug, Clone)]
enum ContentEdit {
    PlayerSpeed(i32),
    Energy { enabled: bool, max: i32, start: Option<i32>, player_max: Option<i32> },
    Collapse { penalty: i64, fraction: u32 },
    Time { start: u32, end: u32, rate: u32 },
    Money(i64),
    InventorySize(u32),
    StartItem { index: usize, quantity: u32 },
    ItemStack { index: usize, max_stack: u32, stackable: bool },
    ItemValue { index: usize, value: i64 },
    ShopPrice { index: usize, price: i64, sell_multiplier: u32 },
    SceneSize { index: usize, width: i32, height: i32 },
    PlayerAt { scene: usize, x: i32, y: i32 },
}

fn content_edit() -> impl Strategy<Value = ContentEdit> {
    prop_oneof![
        prop_oneof![Just(0), 1..=units::TILE, Just(units::TILE * 40)].prop_map(ContentEdit::PlayerSpeed),
        (any::<bool>(), -10..=500i32, proptest::option::of(-50..=600i32), proptest::option::of(-10..=500i32)).prop_map(
            |(enabled, max, start, player_max)| ContentEdit::Energy {
                enabled,
                max: units::points(max),
                start: start.map(units::points),
                player_max: player_max.map(units::points),
            }
        ),
        (-100..=100_000i64, 0..=2000u32).prop_map(|(penalty, fraction)| ContentEdit::Collapse { penalty, fraction }),
        (0..=1500u32, 0..=1600u32, prop_oneof![Just(0u32), 1..=units::MINUTE, Just(units::MINUTE * 120)])
            .prop_map(|(start, end, rate)| ContentEdit::Time { start, end, rate }),
        prop_oneof![-1000..=0i64, 0..=100_000i64].prop_map(ContentEdit::Money),
        (0..=40u32).prop_map(ContentEdit::InventorySize),
        (any::<usize>(), 0..=2500u32).prop_map(|(index, quantity)| ContentEdit::StartItem { index, quantity }),
        (any::<usize>(), 0..=5u32, any::<bool>())
            .prop_map(|(index, max_stack, stackable)| ContentEdit::ItemStack { index, max_stack, stackable }),
        (any::<usize>(), -500..=100_000i64).prop_map(|(index, value)| ContentEdit::ItemValue { index, value }),
        (any::<usize>(), -500..=100_000i64, 0..=5000u32).prop_map(|(index, price, sell_multiplier)| {
            ContentEdit::ShopPrice { index, price, sell_multiplier }
        }),
        (any::<usize>(), -2..=48i32, -2..=48i32).prop_map(|(index, width, height)| ContentEdit::SceneSize {
            index,
            width,
            height
        }),
        (any::<usize>(), -4..=60i32, -4..=60i32).prop_map(|(scene, x, y)| ContentEdit::PlayerAt { scene, x, y }),
    ]
}

fn edit(project: &mut GameProject, edit: &ContentEdit) {
    match *edit {
        ContentEdit::PlayerSpeed(speed) => project.settings.movement.player_speed = speed,
        ContentEdit::Energy { enabled, max, start, player_max } => {
            project.settings.energy_enabled = enabled;
            project.settings.max_energy = max;
            project.player.energy = start;
            project.player.max_energy = player_max;
        }
        ContentEdit::Collapse { penalty, fraction } => {
            project.settings.collapse_money_penalty = penalty;
            project.settings.collapse_energy_fraction = fraction;
        }
        ContentEdit::Time { start, end, rate } => {
            project.settings.time.day_start_minute = start;
            project.settings.time.day_end_minute = end;
            project.settings.time.minutes_per_real_second = rate;
        }
        ContentEdit::Money(money) => project.player.money = money,
        ContentEdit::InventorySize(size) => project.player.max_inventory_size = size,
        ContentEdit::StartItem { index, quantity } => {
            if let Some(item) = pick(&project.items, index).cloned() {
                project.player.inventory.push(InventorySlot { item, quantity, quality: None });
            }
        }
        ContentEdit::ItemStack { index, max_stack, stackable } => {
            let len = project.items.len();
            if len > 0 {
                let item = &mut project.items[index % len];
                item.max_stack = max_stack;
                item.stackable = stackable;
            }
        }
        ContentEdit::ItemValue { index, value } => {
            let len = project.items.len();
            if len > 0 {
                project.items[index % len].value = value;
            }
        }
        ContentEdit::ShopPrice { index, price, sell_multiplier } => {
            let len = project.shops.len();
            if len > 0 {
                let shop = &mut project.shops[index % len];
                shop.sell_price_multiplier = sell_multiplier;
                for entry in &mut shop.stock {
                    entry.price = Some(price);
                }
            }
        }
        ContentEdit::SceneSize { index, width, height } => {
            let len = project.scenes.len();
            if len > 0 {
                let scene = &mut project.scenes[index % len];
                scene.width = width;
                scene.height = height;
            }
        }
        ContentEdit::PlayerAt { scene, x, y } => {
            project.player.scene_id = pick(&project.scenes, scene).map_or_else(|| "nowhere".to_owned(), |s| s.id.clone());
            project.player.x = x;
            project.player.y = y;
        }
    }
}

fn pick<T>(list: &[T], index: usize) -> Option<&T> {
    if list.is_empty() {
        None
    } else {
        list.get(index % list.len())
    }
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 96, ..ProptestConfig::default() })]

    #[test]
    fn generated_content_keeps_the_state_invariants_after_every_step(
        base in 0..BASES.len(),
        edits in proptest::collection::vec(content_edit(), 0..10),
        scripted in any::<bool>(),
        log in proptest::collection::vec(any::<u8>(), 0..400),
    ) {
        let mut project = PROJECTS[base].clone();
        for change in &edits {
            edit(&mut project, change);
        }
        let content = state::create_content_from_project(&project);
        let ids = Ids::of(&content);
        let log = input::inputs(&ids, &mut Choices::new(&log));
        let rules = if scripted { CommandRules::Scripted } else { CommandRules::Player };
        let ctx = EngineContext::with_hooks(content, HookBus::new()).with_rules(rules);
        let mut game = state::create_game_state(&project, Some("invariants"));
        farm_fuzz::play_checked(&ctx, &mut game, &log);
    }
}
