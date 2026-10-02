//! The catalog of engine messages: every sentence the engine itself shows the player (toasts,
//! minigame prompts, save-load notices), each under a stable key with an English template.
//!
//! A [`Message`] is a template plus its arguments. The simulation still records the English
//! text in its effects (`Effect::Message::text`, what replays, goldens and logs keep), and it
//! carries the message beside it, outside serialization and equality, so a player can show the
//! sentence in its own language: `farm-ui` holds a table per language keyed by these keys and
//! falls back to the English text for a key it lacks. Text a creator wrote (event messages,
//! plugin messages, item and NPC names) is not engine text: it passes through as arguments or as
//! a plain `Effect::message`.
//!
//! Templates take positional arguments (`{0}`, `{1}`), so a translation can reorder them. An
//! argument is plain text (a name, a number) or another message (a tool's name, a fragment), which
//! a translation translates too.

use std::fmt::{self, Display};

/// A catalog entry: a stable key and the English template.
#[derive(Debug, PartialEq, Eq)]
pub struct Template {
    pub key: &'static str,
    pub english: &'static str,
}

impl Template {
    /// The message with these plain-text arguments.
    pub fn with(&'static self, args: &[&dyn Display]) -> Message {
        Message { template: self, args: args.iter().map(|arg| Arg::Text(arg.to_string())).collect() }
    }

    /// The message with these arguments (some of them messages themselves).
    pub fn with_args(&'static self, args: Vec<Arg>) -> Message {
        Message { template: self, args }
    }
}

/// One argument of a [`Message`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Arg {
    /// Shown as it is in every language (a name, a number).
    Text(String),
    /// Translated with the message around it.
    Message(Message),
}

impl Arg {
    pub fn text(value: impl Display) -> Self {
        Arg::Text(value.to_string())
    }
}

impl From<Message> for Arg {
    fn from(message: Message) -> Self {
        Arg::Message(message)
    }
}

/// A catalog template with its arguments: a sentence in any language.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    pub template: &'static Template,
    pub args: Vec<Arg>,
}

impl Message {
    pub fn key(&self) -> &'static str {
        self.template.key
    }

    /// The English text (what effects record).
    pub fn english(&self) -> String {
        self.render(&|template| template.english)
    }

    /// The text with each (nested) template looked up by `translate`, which returns the template
    /// text to use for a catalog entry (its English text when the language lacks it).
    pub fn render(&self, translate: &dyn Fn(&'static Template) -> &str) -> String {
        let args: Vec<String> = self
            .args
            .iter()
            .map(|arg| match arg {
                Arg::Text(text) => text.clone(),
                Arg::Message(message) => message.render(translate),
            })
            .collect();
        fill(translate(self.template), &args)
    }
}

impl From<&'static Template> for Message {
    fn from(template: &'static Template) -> Self {
        Message { template, args: Vec::new() }
    }
}

/// Serializes as its English text, so a status that carries a message keeps its JSON shape.
impl serde::Serialize for Message {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.english())
    }
}

/// The message an effect or status was made from, beside its English text. It is presentation
/// data only: equality ignores it (two effects with the same level and text are the same effect,
/// as in a recorded replay), and serialization skips it.
#[derive(Clone, Default)]
pub struct Localized(pub Option<Message>);

impl PartialEq for Localized {
    fn eq(&self, _other: &Self) -> bool {
        true
    }
}

impl fmt::Debug for Localized {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match &self.0 {
            Some(message) => write!(f, "Localized({})", message.key()),
            None => f.write_str("Localized(None)"),
        }
    }
}

/// Replaces `{0}`, `{1}`… in `template` with `args`; a placeholder without an argument, and a
/// brace that starts none, stays. Arguments are not scanned again.
pub fn fill(template: &str, args: &[String]) -> String {
    let mut out = String::with_capacity(template.len() + 16);
    let mut rest = template;
    while let Some(start) = rest.find('{') {
        out.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        let found = after.find('}').and_then(|end| after[..end].parse::<usize>().ok().map(|index| (index, end)));
        match found.and_then(|(index, end)| args.get(index).map(|arg| (arg, end))) {
            Some((arg, end)) => {
                out.push_str(arg);
                rest = &after[end + 1..];
            }
            None => {
                out.push('{');
                rest = after;
            }
        }
    }
    out.push_str(rest);
    out
}

macro_rules! catalog {
    ($($name:ident = $key:literal => $english:literal;)*) => {
        $(pub static $name: Template = Template { key: $key, english: $english };)*
        /// Every template, in catalog order.
        pub static ALL: &[&Template] = &[$(&$name),*];
    };
}

catalog! {
    // Inventory, money, items.
    INVENTORY_FULL = "msg.inventoryFull" => "Inventory is full!";
    INVENTORY_FULL_SHORT = "msg.inventoryFullShort" => "Inventory full!";
    NOT_ENOUGH_MONEY = "msg.notEnoughMoney" => "Not enough money!";
    RECEIVED_ITEM = "msg.receivedItem" => "Received {0}";
    RECEIVED_ITEMS = "msg.receivedItems" => "Received {0} x{1}";
    RECEIVED_COUNT = "msg.receivedCount" => "Received {0}\u{d7} {1}";
    RECEIVED_MONEY = "msg.receivedMoney" => "Received ${0}";
    PAID_MONEY = "msg.paidMoney" => "Paid ${0}";
    PICKED_UP = "msg.pickedUp" => "Picked up {0}";
    DONT_HAVE_ITEM = "msg.dontHaveItem" => "You don't have that item.";
    CANT_USE_LIKE_THAT = "msg.cantUseLikeThat" => "{0} can't be used like that.";
    // Tools and farming.
    NEED_TOOL = "msg.needTool" => "You need a {0}!";
    TOOL_BROKEN = "msg.toolBroken" => "Your {0} is broken! A shop can repair it.";
    CANT_USE_HERE = "msg.cantUseHere" => "Can't use {0} here";
    WATERED = "msg.watered" => "Watered!";
    TILLED = "msg.tilled" => "Tilled soil!";
    CLEARED_WITHERED = "msg.clearedWithered" => "Cleared the withered crop.";
    CROP_NOT_READY = "msg.cropNotReady" => "Crop is not ready to harvest yet";
    CROP_WITHERED = "msg.cropWithered" => "This crop withered \u{2014} clear it with a scythe.";
    HARVEST_ITEM_MISSING = "msg.harvestItemMissing" => "{0} can't be harvested: its harvest item '{1}' is missing.";
    HARVESTED = "msg.harvested" => "Harvested {0}x {1}{2}{3} (worth ~${4})";
    NO_SEEDS = "msg.noSeeds" => "No seeds in inventory";
    INVALID_CROP = "msg.invalidCrop" => "Invalid crop type!";
    CANT_GROW_IN_SEASON = "msg.cantGrowInSeason" => "{0} can't grow in {1}! It grows in {2}.";
    CANNOT_GROW_IN_SEASON = "msg.cannotGrowInSeason" => "{0} cannot grow in {1}!";
    NO_SPACE_FOR_CROP = "msg.noSpaceForCrop" => "Not enough space for this crop!";
    PLANTED = "msg.planted" => "Planted {0}! Water it so it grows.";
    PLANTED_FERTILIZED = "msg.plantedFertilized" => "Planted {0}! (Fertilized) Water it so it grows.";
    NO_SEED_TO_PLANT = "msg.noSeedToPlant" => "You have no {0} to plant.";
    NO_FERTILIZER = "msg.noFertilizer" => "You have no {0} to use.";
    MACHINE_STILL_WORKING_SHORT = "msg.machineStillWorkingShort" => "Still working\u{2026}";
    MACHINE_IDLE = "msg.machineIdle" => "{0} is idle \u{2014} load a recipe.";
    // Tool names (arguments of the tool messages).
    TOOL_WATERING_CAN = "msg.tool.wateringCan" => "watering can";
    TOOL_HOE = "msg.tool.hoe" => "hoe";
    TOOL_AXE = "msg.tool.axe" => "axe";
    TOOL_PICKAXE = "msg.tool.pickaxe" => "pickaxe";
    TOOL_SCYTHE = "msg.tool.scythe" => "scythe";
    TOOL_FISHING_ROD = "msg.tool.fishingRod" => "fishing rod";
    // Built-in seasons, as messages name them (by id).
    SEASON_SPRING = "msg.season.spring" => "spring";
    SEASON_SUMMER = "msg.season.summer" => "summer";
    SEASON_FALL = "msg.season.fall" => "fall";
    SEASON_WINTER = "msg.season.winter" => "winter";
    // Built-in skills, as level-ups name them.
    SKILL_FARMING = "msg.skill.farming" => "Farming";
    SKILL_FISHING = "msg.skill.fishing" => "Fishing";
    SKILL_FORAGING = "msg.skill.foraging" => "Foraging";
    SKILL_MINING = "msg.skill.mining" => "Mining";
    SKILL_SOCIAL = "msg.skill.social" => "Social";
    SKILL_COMBAT = "msg.skill.combat" => "Combat";
    LEVEL_UP = "msg.levelUp" => "{0} level {1}!";
    // Gathering.
    NODE_HEALTH = "msg.nodeHealth" => "{0}: {1}/{2}";
    NODE_NEEDS_TOOL = "msg.nodeNeedsTool" => "{0} needs a {1}.";
    TOOL_TOO_WEAK = "msg.toolTooWeak" => "Your {0} isn't strong enough for {1}.";
    NODE_CLEARED = "msg.nodeCleared" => "{0} cleared!";
    NODE_CLEARED_GOT = "msg.nodeClearedGot" => "{0} cleared! Got {1}";
    // Animals.
    FED_ANIMAL = "msg.fedAnimal" => "Fed {0}";
    COLLECTED_PRODUCT = "msg.collectedProduct" => "Collected {0} from {1}";
    ANIMAL_HAPPY = "msg.animalHappy" => "{0} looks happy! \u{2665}";
    ANIMAL_CONTENT = "msg.animalContent" => "{0} is content.";
    // Crafting and machines.
    CRAFTED = "msg.crafted" => "Crafted {0}x {1}";
    UNKNOWN_RECIPE = "msg.unknownRecipe" => "Unknown recipe.";
    RECIPE_NEEDS_MACHINE = "msg.recipeNeedsMachine" => "That recipe needs a machine \u{2014} load it there.";
    CANNOT_CRAFT = "msg.cannotCraft" => "Cannot craft that right now.";
    RECIPE_LOCKED = "msg.recipeLocked" => "Recipe not unlocked yet.";
    MISSING_INGREDIENTS = "msg.missingIngredients" => "Missing ingredients.";
    NEED_STATION = "msg.needStation" => "You need to be near a {0} to craft that.";
    NEED_STATION_CATEGORY = "msg.needStationCategory" => "You need to be near a {0} station to craft that.";
    UNKNOWN_MACHINE = "msg.unknownMachine" => "Unknown machine.";
    NEED_MACHINE_ITEM = "msg.needMachineItem" => "You need a {0} in your inventory.";
    NO_ROOM_TO_PLACE = "msg.noRoomToPlace" => "No room to place it there.";
    SPOT_MUST_STAY_CLEAR = "msg.spotMustStayClear" => "That spot has to stay clear.";
    PLACED = "msg.placed" => "Placed {0}";
    NO_MACHINE = "msg.noMachine" => "No machine there.";
    MACHINE_STILL_WORKING = "msg.machineStillWorking" => "It is still working.";
    MACHINE_BUSY = "msg.machineBusy" => "It is already working.";
    COLLECT_FIRST = "msg.collectFirst" => "Collect the finished goods first.";
    MACHINE_CANNOT_RUN = "msg.machineCannotRun" => "This machine cannot run that recipe.";
    STARTED_RECIPE = "msg.startedRecipe" => "Started {0}";
    // Shops.
    SHOP_MISSING = "msg.shopMissing" => "That shop does not exist.";
    TALK_TO_SHOPKEEPER = "msg.talkToShopkeeper" => "Talk to the shopkeeper to shop.";
    NO_SHOP_OPEN = "msg.noShopOpen" => "No shop is open.";
    NOT_SOLD_HERE = "msg.notSoldHere" => "Not sold here.";
    NOT_AVAILABLE_IN = "msg.notAvailableIn" => "Not available in {0}.";
    SOLD_OUT = "msg.soldOut" => "Sold out for today!";
    ONLY_LEFT = "msg.onlyLeft" => "Only {0} left today.";
    UNKNOWN_ITEM = "msg.unknownItem" => "Unknown item.";
    BOUGHT = "msg.bought" => "Bought {0}x {1} for ${2}";
    SHOP_DOESNT_BUY = "msg.shopDoesntBuy" => "{0} doesn't buy items.";
    NOT_THAT_MANY = "msg.notThatMany" => "You don't have that many.";
    SOLD = "msg.sold" => "Sold {0}x {1} for ${2}";
    SHOP_DOESNT_REPAIR = "msg.shopDoesntRepair" => "{0} doesn't repair tools.";
    CANT_REPAIR = "msg.cantRepair" => "That can't be repaired.";
    PERFECT_SHAPE = "msg.perfectShape" => "{0} is in perfect shape.";
    REPAIR_TOO_EXPENSIVE = "msg.repairTooExpensive" => "Repair costs ${0} \u{2014} not enough money!";
    REPAIRED = "msg.repaired" => "Repaired {0} for ${1}";
    // Energy, time and the calendar.
    EXHAUSTED = "msg.exhausted" => "You are getting exhausted \u{2014} consider sleeping.";
    COLLAPSED = "msg.collapsed" => "You collapsed from exhaustion! Lost ${0}.";
    WEATHER_TODAY = "msg.weatherToday" => "Weather: {0}";
    SEASON_ARRIVED = "msg.seasonArrived" => "{0} has arrived!";
    YEAR_BEGINS = "msg.yearBegins" => "Year {0} begins!";
    DAY_OF_SEASON = "msg.dayOfSeason" => "Day {0} of {1}, Year {2}";
    FESTIVAL_TODAY = "msg.festivalToday" => "Today is the {0}!";
    // Events, actions and plugins.
    ACTION_CHAIN_LIMIT = "msg.actionChainLimit" => "Action chain limit reached at '{0}': actions may perform actions {1} levels deep and {2} times in all.";
    SOIL_WATERED = "msg.soilWatered" => "The surrounding soil is watered.";
    UNKNOWN_ACTION = "msg.unknownAction" => "Unknown action '{0}'";
    UNKNOWN_MINIGAME = "msg.unknownMinigame" => "Unknown minigame '{0}'";
    NOTHING_TO_PLAY = "msg.nothingToPlay" => "Nothing to play here.";
    PLUGIN_ERROR = "msg.pluginError" => "Plugin {0}: {1}";
    PLUGIN_UNKNOWN_ITEM = "msg.pluginUnknownItem" => "unknown item '{0}'";
    PLUGIN_UNKNOWN_WEATHER = "msg.pluginUnknownWeather" => "unknown weather '{0}'";
    PLUGIN_UNKNOWN_NPC = "msg.pluginUnknownNpc" => "unknown NPC '{0}'";
    // Fishing.
    WATER_QUIET = "msg.waterQuiet" => "The water is quiet \u{2014} nothing seems to live here.";
    FISHED_UP = "msg.fishedUp" => "You fished up {0}\u{2026}";
    NOT_A_NIBBLE = "msg.notANibble" => "Not even a nibble.";
    GOT_AWAY = "msg.gotAway" => "It got away!";
    CAUGHT_FISH = "msg.caughtFish" => "Caught a {0}!";
    // The mine.
    MINE_FLOOR = "msg.mineFloor" => "Mine \u{2014} floor {0}";
    MINE_FLOOR_CHECKPOINT = "msg.mineFloorCheckpoint" => "Mine \u{2014} floor {0} (elevator checkpoint)";
    MINE_NOT_AT_ENTRANCE = "msg.mineNotAtEntrance" => "You need to be at the mine to go down.";
    MINE_NOT_THAT_DEEP = "msg.mineNotThatDeep" => "You haven't found the way down that far yet.";
    MINE_NOT_IN_MINE = "msg.mineNotInMine" => "You're not in the mine.";
    MINE_CLIMB_BACK = "msg.mineClimbBack" => "You climb back to the surface.";
    MINE_LADDER = "msg.mineLadder" => "A ladder to the next floor appears!";
    // Quests.
    QUEST_REWARD_LOST = "msg.questRewardLost" => "Inventory full \u{2014} quest reward lost: {0}\u{d7} {1}";
    NEW_QUEST = "msg.newQuest" => "New quest: {0}";
    // Gifts.
    NO_ONE_TO_GIVE = "msg.noOneToGive" => "No one to give that to.";
    ALREADY_GIFTED = "msg.alreadyGifted" => "{0} has already received a gift today.";
    GIFT_REACTION = "msg.giftReaction" => "{0}: {1}{2} ({3})";
    GIFT_LOVED = "msg.gift.loved" => "They love it!";
    GIFT_LIKED = "msg.gift.liked" => "They like it.";
    GIFT_NEUTRAL = "msg.gift.neutral" => "They accept it politely.";
    GIFT_DISLIKED = "msg.gift.disliked" => "They don't seem thrilled\u{2026}";
    GIFT_HATED = "msg.gift.hated" => "They hate it!";
    GIFT_BIRTHDAY = "msg.gift.birthday" => " (Birthday!)";
    // The world.
    LANDED_ELSEWHERE = "msg.landedElsewhere" => "({0},{1}) in {2} can't be stood on; landed on ({3},{4}) instead.";
    ENTERED = "msg.entered" => "Entered {0}";
    // Built-in minigames (farm-runtime): defaults a minigame's config can replace.
    MINIGAME_READY = "msg.minigame.ready" => "Ready?";
    MINIGAME_GO = "msg.minigame.go" => "Go!";
    TIMING_PROMPT = "msg.minigame.timingPrompt" => "Stop the marker in the zone!";
    TIMING_BUTTON = "msg.minigame.timingButton" => "Stop! (Space)";
    HOLD_PROMPT = "msg.minigame.holdPrompt" => "Hold for {0} seconds, then release to reel in.";
    HOLD_BUTTON = "msg.minigame.holdButton" => "Hold to reel (Space)";
    HOLD_REELING = "msg.minigame.holdReeling" => "Reeling\u{2026} release!";
    BATTLE_STATUS = "msg.minigame.battleStatus" => "You: {0} health \u{b7} {1} magic | {2}: {3} health";
    BATTLE_ENEMY = "msg.minigame.battleEnemy" => "Forest slime";
    BATTLE_HEAVY = "msg.minigame.battleHeavy" => "{0} is preparing a heavy attack. Guard next turn!";
    BATTLE_ATTACKS = "msg.minigame.battleAttacks" => "{0} attacks. Choose your next move.";
    RHYTHM_PROMPT = "msg.minigame.rhythmPrompt" => "Tap when a note reaches the line!";
    RHYTHM_BUTTON = "msg.minigame.rhythmButton" => "Tap! (Space)";
    RHYTHM_HITS = "msg.minigame.rhythmHits" => "Hits: {0}/{1}";
    MOVING_PROMPT = "msg.minigame.movingPrompt" => "Keep the bar under the target: hold to move right, let go to drift left.";
    MOVING_BUTTON = "msg.minigame.movingButton" => "Hold to move (Space)";
    MOVING_TIME_LEFT = "msg.minigame.movingTimeLeft" => "{0} s left";
    MEMORY_PROMPT = "msg.minigame.memoryPrompt" => "Watch the symbols, then repeat them in order.";
    MEMORY_WATCH = "msg.minigame.memoryWatch" => "Watch closely\u{2026}";
    MEMORY_TURN = "msg.minigame.memoryTurn" => "Your turn: {0}/{1}";
    // Loading saves (farm-cart).
    SAVE_DAMAGED = "msg.save.damaged" => "{0}";
    SAVE_TOO_LARGE = "msg.save.tooLarge" => "Save file is too large.";
    SAVE_STATE_TOO_LARGE = "msg.save.stateTooLarge" => "Save state is too large.";
    SAVE_NOT_A_SAVE = "msg.save.notASave" => "Not a Farm Engine save (FGSV identifier missing).";
    SAVE_FORMAT_NEWER = "msg.save.formatNewer" => "Save file format {0} is newer than this player supports ({1}). Update the game.";
    SAVE_OTHER_GAME = "msg.save.otherGame" => "This save belongs to a different game ('{0}', not '{1}').";
    SAVE_NEWER_VERSION = "msg.save.newerVersion" => "This save was made with a newer version of the game ({0}, this is {1}). Some progress may not load.";
    SAVE_MAP_REPAIRED = "msg.save.mapRepaired" => "The map of {0} in this save did not match its size and was repaired.";
    SAVE_NEW_RNG = "msg.save.newRng" => "This save had no random number state; a new one was started.";
    SAVE_ITEM_SET_ASIDE = "msg.save.itemSetAside" => "{0} item in this save no longer exists in the game and was set aside: {1}.";
    SAVE_ITEMS_SET_ASIDE = "msg.save.itemsSetAside" => "{0} items in this save no longer exist in the game and were set aside: {1}.";
}

/// The name of a tool type as messages say it: a message for the built-in tools, else the type
/// with its first dash as a space ("fishing-rod-2" → "fishing rod-2").
pub fn tool_noun(tool_type: &str) -> Arg {
    let template = match tool_type {
        "watering-can" => &TOOL_WATERING_CAN,
        "hoe" => &TOOL_HOE,
        "axe" => &TOOL_AXE,
        "pickaxe" => &TOOL_PICKAXE,
        "scythe" => &TOOL_SCYTHE,
        "fishing-rod" => &TOOL_FISHING_ROD,
        _ => return Arg::Text(tool_type.replacen('-', " ", 1)),
    };
    Arg::Message(template.into())
}

/// A season id as messages say it: a message for the built-in seasons, else the id.
pub fn season_noun(season: &str) -> Arg {
    let template = match season {
        "spring" => &SEASON_SPRING,
        "summer" => &SEASON_SUMMER,
        "fall" => &SEASON_FALL,
        "winter" => &SEASON_WINTER,
        _ => return Arg::Text(season.to_owned()),
    };
    Arg::Message(template.into())
}

/// A skill as level-ups name it: a message for the built-in skills, else the id with its first
/// letter in upper case.
pub fn skill_noun(skill: &str) -> Arg {
    let template = match skill {
        "farming" => &SKILL_FARMING,
        "fishing" => &SKILL_FISHING,
        "foraging" => &SKILL_FORAGING,
        "mining" => &SKILL_MINING,
        "social" => &SKILL_SOCIAL,
        "combat" => &SKILL_COMBAT,
        _ => {
            let mut chars = skill.chars();
            return Arg::Text(match chars.next() {
                Some(first) => first.to_uppercase().chain(chars).collect(),
                None => String::new(),
            });
        }
    };
    Arg::Message(template.into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeSet;

    #[test]
    fn keys_are_unique_and_namespaced() {
        let keys: BTreeSet<&str> = ALL.iter().map(|template| template.key).collect();
        assert_eq!(keys.len(), ALL.len(), "duplicate key");
        assert!(ALL.iter().all(|template| template.key.starts_with("msg.")));
    }

    #[test]
    fn english_fills_arguments_and_nested_messages() {
        assert_eq!(NOT_ENOUGH_MONEY.with(&[]).english(), "Not enough money!");
        assert_eq!(BOUGHT.with(&[&3, &"Wheat", &30]).english(), "Bought 3x Wheat for $30");
        assert_eq!(NEED_TOOL.with_args(vec![tool_noun("watering-can")]).english(), "You need a watering can!");
        assert_eq!(NEED_TOOL.with_args(vec![tool_noun("fishing-rod-2")]).english(), "You need a fishing rod-2!");
        // Arguments are not scanned for placeholders again.
        assert_eq!(fill("{0} {1}", &["{1}".to_owned(), "b".to_owned()]), "{1} b");
        assert_eq!(fill("{x} {2} {", &["a".to_owned()]), "{x} {2} {");
        let translated = NEED_TOOL.with_args(vec![tool_noun("hoe")]).render(&|template| {
            if template.key == "msg.tool.hoe" {
                "azada"
            } else {
                "Necesitas {0}"
            }
        });
        assert_eq!(translated, "Necesitas azada");
    }

    #[test]
    fn localized_never_changes_equality() {
        assert_eq!(Localized(Some(Message::from(&WATERED))), Localized(None));
    }
}
