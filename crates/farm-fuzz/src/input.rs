//! Fuzz bytes as game input: a replay log of commands (with arguments drawn from the content's
//! ids, plus an id no content has) and ticks. Every byte string decodes to some log, so the
//! fuzzer explores play instead of a parser.

use farm_sim::replay::ReplayInput;
use farm_sim::schema::{tool_types, GameContent, PluginMutation, SKILL_NAMES};
use farm_sim::{units, Command};
use serde_json::Value;

/// The most inputs one log decodes to (a fuzz case stays fast).
pub const MAX_INPUTS: usize = 96;

/// The most ticks one input advances (30 seconds of play).
pub const MAX_TICKS: u64 = 600;

/// An id no content has: every lookup by id must fail soft on it.
pub const UNKNOWN_ID: &str = "no-such-id";

/// Reads bytes as a sequence of choices; past the end every read is 0.
#[derive(Debug, Clone)]
pub struct Choices<'a> {
    data: &'a [u8],
    at: usize,
}

impl<'a> Choices<'a> {
    /// Choices over `data`.
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, at: 0 }
    }

    /// Whether every byte was read.
    pub fn is_empty(&self) -> bool {
        self.at >= self.data.len()
    }

    /// The next byte (0 past the end).
    pub fn byte(&mut self) -> u8 {
        let byte = self.data.get(self.at).copied().unwrap_or(0);
        self.at = self.at.saturating_add(1);
        byte
    }

    /// The next four bytes, little-endian.
    pub fn u32(&mut self) -> u32 {
        u32::from_le_bytes([self.byte(), self.byte(), self.byte(), self.byte()])
    }

    /// A number in `0..n` (0 when `n` is 0).
    pub fn below(&mut self, n: usize) -> usize {
        if n == 0 {
            return 0;
        }
        let wide = if n > 256 { u32::from(self.byte()) << 8 | u32::from(self.byte()) } else { u32::from(self.byte()) };
        wide as usize % n
    }

    /// A number in `min..=max`, mostly small ones near `min`, sometimes the extremes.
    pub fn int(&mut self, min: i32, max: i32) -> i32 {
        match self.byte() {
            0xFE => max,
            0xFF => min,
            byte => {
                let span = i64::from(max) - i64::from(min) + 1;
                (i64::from(min) + i64::from(byte) % span.max(1)) as i32
            }
        }
    }

    /// One of `ids`, or [`UNKNOWN_ID`].
    pub fn pick(&mut self, ids: &[String]) -> String {
        let index = self.below(ids.len() + 1);
        ids.get(index).cloned().unwrap_or_else(|| UNKNOWN_ID.to_owned())
    }

    /// One of `names`.
    pub fn pick_str(&mut self, names: &[&str]) -> String {
        names[self.below(names.len())].to_owned()
    }
}

/// The ids a game's commands can name.
#[derive(Debug, Clone, Default)]
pub struct Ids {
    pub items: Vec<String>,
    pub shops: Vec<String>,
    pub recipes: Vec<String>,
    pub machines: Vec<String>,
    pub npcs: Vec<String>,
    pub actions: Vec<String>,
    pub minigames: Vec<String>,
    pub weather: Vec<String>,
    pub quests: Vec<String>,
    pub scenes: Vec<String>,
}

impl Ids {
    /// The ids of `content`.
    pub fn of(content: &GameContent) -> Self {
        fn ids<T>(list: &[T], id: impl Fn(&T) -> &str) -> Vec<String> {
            list.iter().map(|def| id(def).to_owned()).collect()
        }
        Self {
            items: ids(&content.items, |d| &d.id),
            shops: ids(&content.shops, |d| &d.id),
            recipes: ids(&content.recipes, |d| &d.id),
            machines: ids(&content.machine_types, |d| &d.id),
            npcs: ids(&content.npcs, |d| &d.id),
            actions: ids(&content.actions, |d| &d.id),
            minigames: ids(&content.minigames, |d| &d.id),
            weather: ids(&content.weather.types, |d| &d.id),
            quests: ids(&content.quests, |d| &d.id),
            scenes: ids(&content.scenes, |d| &d.id),
        }
    }
}

const DIRECTIONS: [&str; 5] = ["up", "down", "left", "right", "sideways"];

/// A plugin mutation with arguments from `ids` (the schema bounds of `validate_mutations`, and
/// past them).
pub fn plugin_mutation(ids: &Ids, c: &mut Choices<'_>) -> PluginMutation {
    match c.below(16) {
        0 => PluginMutation::GiveItem { item_id: c.pick(&ids.items), quantity: c.int(1, 999) as u32 },
        1 => PluginMutation::TakeItem { item_id: c.pick(&ids.items), quantity: c.int(1, 999) as u32 },
        2 => PluginMutation::GiveMoney { amount: i64::from(c.int(1, 1_000_000)) },
        3 => PluginMutation::TakeMoney { amount: i64::from(c.int(1, 1_000_000)) },
        4 => {
            let value = match c.below(5) {
                0 => Value::Bool(c.byte().is_multiple_of(2)),
                1 => Value::from(c.int(-1000, 1000)),
                2 => Value::from(f64::from(c.int(-1000, 1000)) / 8.0),
                3 => Value::from(f64::from(c.int(0, 100))),
                _ => Value::from("text"),
            };
            PluginMutation::SetFlag { flag: c.pick_str(&["flag-a", "flag-b", ""]), value }
        }
        5 => PluginMutation::Message { text: "hello".to_owned() },
        6 => PluginMutation::SetWeather { weather_id: c.pick(&ids.weather) },
        7 => PluginMutation::ModifyFriendship { npc_id: c.pick(&ids.npcs), delta: c.int(-1000, 1000) },
        8 => PluginMutation::GrantXp { skill: c.pick_str(SKILL_NAMES), amount: c.int(1, 100_000) as u32 },
        9 => PluginMutation::ModifyEnergy { delta: units::points(c.int(-1000, 1000)) },
        10 => PluginMutation::StartQuest { quest_id: c.pick(&ids.quests) },
        11 => PluginMutation::WarpPlayer { scene_id: c.pick(&ids.scenes), x: c.int(-2, 300), y: c.int(-2, 300) },
        12 => PluginMutation::StartDialogue { npc_id: c.pick(&ids.npcs), dialogue_id: None },
        13 => PluginMutation::PlaySound { sound_id: "sound".to_owned() },
        14 => PluginMutation::PerformAction { action_id: c.pick(&ids.actions) },
        _ => PluginMutation::StartMinigame { minigame_id: c.pick(&ids.minigames) },
    }
}

/// One command with arguments from `ids`.
pub fn command(ids: &Ids, c: &mut Choices<'_>) -> Command {
    match c.below(28) {
        0..=2 => Command::SetMoveIntent { dx: c.int(-2, 2), dy: c.int(-2, 2) },
        3..=5 => Command::Move { dir: c.pick_str(&DIRECTIONS) },
        6..=8 => {
            let mut tools: Vec<&str> = tool_types::ALL.to_vec();
            tools.push("spoon");
            Command::UseTool { tool: c.pick_str(&tools) }
        }
        9 | 10 => Command::Interact,
        11 => {
            let seed_item_id = (c.byte().is_multiple_of(2)).then(|| c.pick(&ids.items));
            let fertilizer_item_id = (c.byte().is_multiple_of(2)).then(|| c.pick(&ids.items));
            Command::InteractWith { seed_item_id, fertilizer_item_id }
        }
        12 => Command::ChooseDialogueOption { index: c.int(-1, 5) },
        13 => Command::CloseDialogue,
        14 => Command::Sleep,
        15 => Command::OpenShop { shop_id: c.pick(&ids.shops) },
        16 => Command::CloseShop,
        17 => Command::BuyItem { item_id: c.pick(&ids.items), quantity: c.int(0, 2000) as u32 },
        18 => {
            let quality = (c.byte().is_multiple_of(2)).then(|| c.pick_str(&["silver", "gold", "iridium", "plastic"]));
            Command::SellItem { item_id: c.pick(&ids.items), quantity: c.int(0, 2000) as u32, quality }
        }
        19 => Command::RepairTool { item_id: c.pick(&ids.items) },
        20 => Command::Craft { recipe_id: c.pick(&ids.recipes) },
        21 => match c.below(3) {
            0 => Command::PlaceMachine { machine_type_id: c.pick(&ids.machines) },
            1 => Command::MachineLoad { recipe_id: c.pick(&ids.recipes) },
            _ => Command::PickUpMachine,
        },
        22 => Command::GiveGift { item_id: c.pick(&ids.items) },
        23 => match c.below(2) {
            0 => Command::DescendMine { floor: c.int(0, 1000) as u32 },
            _ => Command::ExitMine,
        },
        24 => match c.below(2) {
            0 => Command::PerformAction { action_id: c.pick(&ids.actions) },
            _ => Command::UseItem { item_id: c.pick(&ids.items) },
        },
        25 => match c.below(3) {
            0 => Command::StartMinigame { minigame_id: c.pick(&ids.minigames) },
            1 => Command::ResolveMinigame { score: u64::from(c.u32()) % (units::PROBABILITY_ONE + 1) },
            _ => Command::CancelMinigame,
        },
        _ => Command::PluginMutation {
            plugin_id: c.pick_str(&["plugin-a", "plugin-b"]),
            mutation: plugin_mutation(ids, c),
        },
    }
}

/// A replay log: commands and ticks, at most [`MAX_INPUTS`] inputs (fewer when the bytes run
/// out).
pub fn inputs(ids: &Ids, c: &mut Choices<'_>) -> Vec<ReplayInput> {
    let mut log = Vec::new();
    while !c.is_empty() && log.len() < MAX_INPUTS {
        if c.byte().is_multiple_of(5) {
            let ticks = match c.byte() {
                0xFF => MAX_TICKS,
                byte => u64::from(byte % 120) + 1,
            };
            log.push(ReplayInput::Tick { ticks });
        } else {
            log.push(ReplayInput::Command { command: command(ids, c) });
        }
    }
    log
}
