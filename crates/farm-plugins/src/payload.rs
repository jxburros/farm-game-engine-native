//! Hook payload JSON as plugins see it.
//!
//! Plugins must see the same payload text on every host, key order included (a plugin may
//! `JSON.stringify` its payload into a flag). The web engine builds payload objects in a
//! fixed order; the Rust payload structs declare their fields in that order, and this module
//! writes them like `crates/farm-host`'s `view_json` does for the C# bridge: fields in
//! declaration order and numbers and strings as JavaScript's `JSON.stringify` writes them.

use farm_sim::effects::Effect;
use farm_sim::hooks::{EffectHookPayload, HookEvent};
use farm_sim::js;
use serde::Serialize;
use serde_json::Value;

/// JSON text of `value` in engine order, with JavaScript number formatting.
pub fn to_engine_json<T: Serialize>(value: &T) -> String {
    let json = serde_json::to_value(value).expect("engine types always serialize");
    let mut out = String::new();
    write(&mut out, &json);
    out
}

/// The payload of a hook event as JSON text (`{"day":2,"season":"spring","year":1}` for
/// `onDayStart`).
pub fn hook_payload_json(event: &HookEvent) -> String {
    let mut tagged = serde_json::to_value(event).expect("hook events always serialize");
    let payload = tagged.get_mut("payload").map(Value::take).unwrap_or(Value::Null);
    let mut out = String::new();
    write(&mut out, &payload);
    out
}

/// The observe-only `onEffect` events a host emits after a step, one per effect in order
/// (the web app and game shell emit `{ effectType }` for every effect they handle).
pub fn effect_events(effects: &[Effect]) -> Vec<HookEvent> {
    effects
        .iter()
        .map(|effect| HookEvent::Effect(EffectHookPayload { effect_type: effect.type_name().to_owned() }))
        .collect()
}

fn write(out: &mut String, value: &Value) {
    match value {
        Value::Object(map) => {
            out.push('{');
            for (i, (name, item)) in map.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                js::push_quoted(out, name);
                out.push(':');
                write(out, item);
            }
            out.push('}');
        }
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write(out, item);
            }
            out.push(']');
        }
        Value::String(text) => js::push_quoted(out, text),
        // JSON.stringify writes non-finite numbers as null.
        Value::Number(number) => match number.as_f64() {
            Some(n) if n.is_finite() => out.push_str(&js::num(n)),
            _ => out.push_str("null"),
        },
        Value::Bool(true) => out.push_str("true"),
        Value::Bool(false) => out.push_str("false"),
        Value::Null => out.push_str("null"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use farm_sim::hooks::{DayHookPayload, GatherDrop, ResourceGatherHookPayload, WeatherRollHookPayload};

    #[test]
    fn payloads_keep_engine_order_and_javascript_numbers() {
        let day = HookEvent::DayStart(DayHookPayload { day: 2.0, season: "spring".to_owned(), year: 1.0 });
        assert_eq!(hook_payload_json(&day), r#"{"day":2,"season":"spring","year":1}"#);
        let weather = HookEvent::WeatherRoll(WeatherRollHookPayload { weather_id: "rain".to_owned(), day: 3.0 });
        assert_eq!(hook_payload_json(&weather), r#"{"weatherId":"rain","day":3}"#);
        let gather = HookEvent::ResourceGather(ResourceGatherHookPayload {
            node_type_id: "node-tree".to_owned(),
            drops: vec![GatherDrop { item_id: "material-wood".to_owned(), quantity: 2.5 }],
        });
        assert_eq!(
            hook_payload_json(&gather),
            r#"{"nodeTypeId":"node-tree","drops":[{"itemId":"material-wood","quantity":2.5}]}"#
        );
    }

    #[test]
    fn effect_events_name_each_effect() {
        let effects = vec![Effect::message("info", "hi"), Effect::Sound { id: "coin".to_owned() }];
        let events = effect_events(&effects);
        let payloads: Vec<String> = events.iter().map(hook_payload_json).collect();
        assert_eq!(payloads, [r#"{"effectType":"message"}"#, r#"{"effectType":"sound"}"#]);
        assert!(events.iter().all(|event| event.hook() == "onEffect"));
    }
}
