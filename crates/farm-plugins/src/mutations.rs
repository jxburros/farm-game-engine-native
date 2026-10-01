//! Validation of what handlers return (port of `Plugins.ValidateMutations` /
//! `Plugins.TryParseMutation`, which mirror zod's `PluginMutationSchema.safeParse` in
//! `packages/engine-schemas/src/packs.ts`).
//!
//! A handler's result must be an array; each entry is parsed on its own and invalid ones are
//! dropped with an [`PluginErrorKind::InvalidMutation`] error. The rules: discriminated on
//! `type`; required fields type-checked; integers and ranges enforced; message text at most 500
//! UTF-16 code units (JavaScript string length); flag values boolean, number or string; unknown
//! keys stripped. Error texts are the C# host's, word for word.

use crate::{PluginError, PluginErrorKind};
use farm_sim::schema::PluginMutation;
use farm_sim::units;
use serde_json::{Map, Value};

/// The largest tile coordinate a warp may name: the last tile of the largest scene.
const MAX_TILE: f64 = (farm_sim::schema::MAX_SCENE_SIZE - 1) as f64;

/// Validate a handler's return value. A non-array yields nothing; each entry is parsed with
/// [`parse_mutation`], and invalid ones are dropped with an error
/// `mutation [{index}] dropped: {reason}`.
pub fn validate_mutations(plugin_id: &str, raw: &Value, hook: Option<&str>) -> (Vec<PluginMutation>, Vec<PluginError>) {
    let mut mutations = Vec::new();
    let mut errors = Vec::new();
    let Value::Array(entries) = raw else {
        return (mutations, errors);
    };
    for (index, entry) in entries.iter().enumerate() {
        match parse_mutation(entry) {
            Ok(mutation) => mutations.push(mutation),
            Err(reason) => errors.push(PluginError {
                plugin_id: plugin_id.to_owned(),
                kind: PluginErrorKind::InvalidMutation,
                message: format!("mutation [{index}] dropped: {reason}"),
                hook: hook.map(str::to_owned),
            }),
        }
    }
    (mutations, errors)
}

/// Parse one mutation exactly like zod's `PluginMutationSchema.safeParse`. `Err` carries the
/// reason, e.g. `giveMoney: amount: expected integer`.
pub fn parse_mutation(entry: &Value) -> Result<PluginMutation, String> {
    let Value::Object(object) = entry else {
        return Err(format!("expected object, received {}", describe(entry)));
    };
    let Some(Value::String(kind)) = object.get("type") else {
        return Err("invalid discriminator value (type)".to_owned());
    };
    let mut fields = Fields { object, failures: Vec::new() };
    let parsed = match kind.as_str() {
        "giveItem" => {
            let item_id = fields.string("itemId");
            PluginMutation::GiveItem { item_id, quantity: fields.int("quantity", 1.0, 999.0) as u32 }
        }
        "takeItem" => {
            let item_id = fields.string("itemId");
            PluginMutation::TakeItem { item_id, quantity: fields.int("quantity", 1.0, 999.0) as u32 }
        }
        "giveMoney" => PluginMutation::GiveMoney { amount: fields.int("amount", 1.0, 1_000_000.0) as i64 },
        "takeMoney" => PluginMutation::TakeMoney { amount: fields.int("amount", 1.0, 1_000_000.0) as i64 },
        "setFlag" => {
            let flag = fields.string("flag");
            PluginMutation::SetFlag { flag, value: fields.flag_value() }
        }
        "message" => {
            let text = fields.string("text");
            fields.max_length(&text, 500, "text");
            PluginMutation::Message { text }
        }
        "setWeather" => PluginMutation::SetWeather { weather_id: fields.string("weatherId") },
        "modifyFriendship" => {
            let npc_id = fields.string("npcId");
            PluginMutation::ModifyFriendship { npc_id, delta: fields.int("delta", -1000.0, 1000.0) as i32 }
        }
        "grantXp" => {
            let skill = fields.string("skill");
            PluginMutation::GrantXp { skill, amount: fields.int("amount", 1.0, 10_000.0) as u32 }
        }
        "modifyEnergy" => {
            // Whole energy points, stored in thousandths.
            PluginMutation::ModifyEnergy { delta: units::points(fields.int("delta", -1000.0, 1000.0) as i32) }
        }
        "startQuest" => PluginMutation::StartQuest { quest_id: fields.string("questId") },
        "warpPlayer" => {
            // A tile of the largest scene there can be (the engine also lands a warp outside the
            // scene, or on a blocked tile, on the nearest walkable one).
            let scene_id = fields.string("sceneId");
            let x = fields.int("x", 0.0, MAX_TILE) as i32;
            PluginMutation::WarpPlayer { scene_id, x, y: fields.int("y", 0.0, MAX_TILE) as i32 }
        }
        "startDialogue" => {
            let npc_id = fields.string("npcId");
            PluginMutation::StartDialogue { npc_id, dialogue_id: fields.optional_string("dialogueId") }
        }
        "playSound" => PluginMutation::PlaySound { sound_id: fields.string("soundId") },
        "performAction" => PluginMutation::PerformAction { action_id: fields.string("actionId") },
        "startMinigame" => PluginMutation::StartMinigame { minigame_id: fields.string("minigameId") },
        other => return Err(format!("invalid discriminator value (type '{other}')")),
    };
    if fields.failures.is_empty() {
        Ok(parsed)
    } else {
        Err(format!("{kind}: {}", fields.failures.join("; ")))
    }
}

/// The C# `Describe(JsonElement)`: the JSON kind of a non-object entry.
fn describe(value: &Value) -> &'static str {
    match value {
        Value::Array(_) => "array",
        Value::Null => "null",
        Value::String(_) => "string",
        Value::Number(_) => "number",
        Value::Bool(_) => "boolean",
        Value::Object(_) => "object",
    }
}

/// Field readers that collect failures in order (C# `Str`, `OptStr`, `Int`, …).
struct Fields<'a> {
    object: &'a Map<String, Value>,
    failures: Vec<String>,
}

impl Fields<'_> {
    fn string(&mut self, key: &str) -> String {
        match self.object.get(key) {
            Some(Value::String(text)) => text.clone(),
            _ => {
                self.failures.push(format!("{key}: expected string"));
                String::new()
            }
        }
    }

    /// zod `.optional()`: absent is fine, `null` is not.
    fn optional_string(&mut self, key: &str) -> Option<String> {
        match self.object.get(key) {
            None => None,
            Some(Value::String(text)) => Some(text.clone()),
            Some(_) => {
                self.failures.push(format!("{key}: expected string"));
                None
            }
        }
    }

    /// zod `.number().int().min(min).max(max)` (the caller casts a valid value to its integer
    /// type; an invalid one fails the whole mutation).
    fn int(&mut self, key: &str, min: f64, max: f64) -> f64 {
        let Some(n) = self.object.get(key).and_then(Value::as_f64) else {
            self.failures.push(format!("{key}: expected number"));
            return 0.0;
        };
        if !units::is_integer(n) {
            self.failures.push(format!("{key}: expected integer"));
        } else if n < min {
            self.failures.push(format!("{key}: must be >= {}", units::format_number(min)));
        } else if n > max {
            self.failures.push(format!("{key}: must be <= {}", units::format_number(max)));
        }
        n
    }

    /// `z.union([z.boolean(), z.number(), z.string()])`.
    fn flag_value(&mut self) -> Value {
        match self.object.get("value") {
            Some(value @ (Value::Bool(_) | Value::Number(_) | Value::String(_))) => value.clone(),
            _ => {
                self.failures.push("value: expected boolean | number | string".to_owned());
                Value::Null
            }
        }
    }

    /// zod `.max(n)` on a string: JavaScript length, in UTF-16 code units.
    fn max_length(&mut self, value: &str, max: usize, key: &str) {
        if value.encode_utf16().count() > max {
            self.failures.push(format!("{key}: at most {max} characters"));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn reason(value: Value) -> String {
        parse_mutation(&value).expect_err("should be invalid")
    }

    #[test]
    fn mirrors_the_zod_schema() {
        // C# TryParseMutationMirrorsTheZodSchema.
        let ok = |value: Value| parse_mutation(&value).is_ok();
        assert!(ok(json!({"type":"giveItem","itemId":"a","quantity":999})));
        assert!(!ok(json!({"type":"giveItem","itemId":"a"})));
        assert!(ok(json!({"type":"modifyEnergy","delta":-1000})));
        assert!(!ok(json!({"type":"modifyEnergy","delta":-1001})));
        assert!(ok(json!({"type":"setFlag","flag":"f","value":false})));
        assert!(ok(json!({"type":"setFlag","flag":"f","value":0})));
        assert!(ok(json!({"type":"startDialogue","npcId":"n","dialogueId":"d"})));
        assert!(!ok(json!({"type":"startDialogue","npcId":"n","dialogueId":null})));
        assert!(ok(json!({"type":"message","text":""})));
        assert!(!ok(json!({"type":"MESSAGE","text":""})));
    }

    #[test]
    fn error_texts_match_the_csharp_host() {
        assert_eq!(reason(json!({"type":"giveMoney","amount":1.5})), "giveMoney: amount: expected integer");
        assert_eq!(reason(json!({"type":"giveMoney","amount":0})), "giveMoney: amount: must be >= 1");
        assert_eq!(reason(json!({"type":"giveMoney","amount":"100"})), "giveMoney: amount: expected number");
        assert_eq!(reason(json!({"type":"takeMoney","amount":1_000_001})), "takeMoney: amount: must be <= 1000000");
        assert_eq!(
            reason(json!({"type":"giveItem","itemId":7,"quantity":1000})),
            "giveItem: itemId: expected string; quantity: must be <= 999"
        );
        assert_eq!(reason(json!({"type":"message","text":"x".repeat(501)})), "message: text: at most 500 characters");
        assert_eq!(
            reason(json!({"type":"setFlag","flag":"f","value":null})),
            "setFlag: value: expected boolean | number | string"
        );
        assert_eq!(
            reason(json!({"type":"setFlag","value":{"nested":true}})),
            "setFlag: flag: expected string; value: expected boolean | number | string"
        );
        assert_eq!(
            reason(json!({"type":"startDialogue","npcId":"n","dialogueId":7})),
            "startDialogue: dialogueId: expected string"
        );
        assert_eq!(reason(json!({"type":"warpPlayer","sceneId":"s","x":-1,"y":0})), "warpPlayer: x: must be >= 0");
        assert_eq!(reason(json!({"type":"modifyEnergy","delta":-1001})), "modifyEnergy: delta: must be >= -1000");
        assert_eq!(
            reason(json!({"type":"setState","path":"player.money"})),
            "invalid discriminator value (type 'setState')"
        );
        assert_eq!(reason(json!({"amount":5})), "invalid discriminator value (type)");
        assert_eq!(reason(json!({"type":7})), "invalid discriminator value (type)");
        assert_eq!(reason(json!("giveMoney")), "expected object, received string");
        assert_eq!(reason(json!(null)), "expected object, received null");
        assert_eq!(reason(json!([1, 2])), "expected object, received array");
        assert_eq!(reason(json!(3)), "expected object, received number");
        assert_eq!(reason(json!(true)), "expected object, received boolean");
    }

    #[test]
    fn message_length_counts_utf16_code_units() {
        // 250 astral characters are 500 UTF-16 code units (1000 UTF-8 bytes): allowed.
        assert!(parse_mutation(&json!({"type":"message","text":"🌾".repeat(250)})).is_ok());
        assert_eq!(reason(json!({"type":"message","text":"🌾".repeat(251)})), "message: text: at most 500 characters");
    }

    #[test]
    fn unknown_keys_are_stripped_and_values_typed() {
        let parsed =
            parse_mutation(&json!({"type":"giveItem","itemId":"seed-wheat","quantity":2,"extra":"x"})).unwrap();
        assert_eq!(parsed, PluginMutation::GiveItem { item_id: "seed-wheat".to_owned(), quantity: 2 });
        let parsed = parse_mutation(&json!({"type":"startDialogue","npcId":"n"})).unwrap();
        assert_eq!(parsed, PluginMutation::StartDialogue { npc_id: "n".to_owned(), dialogue_id: None });
        let parsed = parse_mutation(&json!({"type":"warpPlayer","sceneId":"s","x":255,"y":0})).unwrap();
        assert_eq!(parsed, PluginMutation::WarpPlayer { scene_id: "s".to_owned(), x: 255, y: 0 });
        // Warps stay within the largest scene.
        assert_eq!(reason(json!({"type":"warpPlayer","sceneId":"s","x":1e6,"y":0})), "warpPlayer: x: must be <= 255");
    }

    #[test]
    fn validate_mutations_keeps_valid_entries_and_reports_the_rest() {
        let raw = json!([{"type":"message","text":"ok"}, {"type":"giveMoney","amount":0}, null]);
        let (mutations, errors) = validate_mutations("pack:p", &raw, Some("onDayStart"));
        assert_eq!(mutations, vec![PluginMutation::Message { text: "ok".to_owned() }]);
        let messages: Vec<&str> = errors.iter().map(|e| e.message.as_str()).collect();
        assert_eq!(
            messages,
            [
                "mutation [1] dropped: giveMoney: amount: must be >= 1",
                "mutation [2] dropped: expected object, received null"
            ]
        );
        assert!(errors.iter().all(|e| e.kind == PluginErrorKind::InvalidMutation
            && e.plugin_id == "pack:p"
            && e.hook.as_deref() == Some("onDayStart")));

        // Anything but an array yields nothing, without errors.
        for raw in [json!({"type":"giveMoney","amount":5}), json!(null), json!("[]")] {
            let (mutations, errors) = validate_mutations("pack:p", &raw, None);
            assert!(mutations.is_empty() && errors.is_empty());
        }
    }
}
