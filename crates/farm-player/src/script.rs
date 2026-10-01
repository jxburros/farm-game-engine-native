//! Scripted input for windowless runs (`--screenshot … --script inputs.json`, tests).
//!
//! ```json
//! { "steps": [
//!   { "at": 5, "press": "enter" },
//!   { "at": 30, "events": [{ "type": "keyDown", "key": "d" }] },
//!   { "at": 60, "events": [{ "type": "keyUp", "key": "d" }] },
//!   { "at": 70, "button": "south" },
//!   { "at": 80, "click": [640, 400] }
//! ] }
//! ```
//!
//! `at` is the frame index (60 frames a second). `press` is a key down on that frame and up on
//! the next; `button` does the same for a gamepad button and `click` for the pointer.

use crate::input::{InputEvent, PointerButton};
use crate::player::{Player, PlayerError};
use farm_render::tiny_skia::Pixmap;
use farm_ui::GamepadButton;
use serde::Deserialize;

/// Fixed frame time of scripted runs.
pub const FRAME_SECONDS: f64 = 1.0 / 60.0;

#[derive(Debug, Clone, PartialEq, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ScriptStep {
    pub at: u32,
    #[serde(default)]
    pub events: Vec<InputEvent>,
    #[serde(default)]
    pub press: Option<String>,
    #[serde(default)]
    pub button: Option<GamepadButton>,
    #[serde(default)]
    pub click: Option<[f32; 2]>,
}

#[derive(Debug, Clone, PartialEq, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InputScript {
    #[serde(default)]
    pub steps: Vec<ScriptStep>,
}

impl InputScript {
    pub fn parse(json: &str) -> Result<Self, String> {
        serde_json::from_str(json).map_err(|error| format!("Input script: {error}"))
    }

    /// The events of frame `frame`, in script order.
    pub fn events_at(&self, frame: u32) -> Vec<InputEvent> {
        let mut events = Vec::new();
        for step in &self.steps {
            if step.at == frame {
                events.extend(step.events.iter().cloned());
                if let Some(key) = &step.press {
                    events.push(InputEvent::KeyDown { key: key.clone(), repeat: false });
                }
                if let Some(button) = step.button {
                    events.push(InputEvent::GamepadButton { button, pressed: true });
                }
                if let Some([x, y]) = step.click {
                    events.push(InputEvent::PointerMove { x, y });
                    events.push(InputEvent::PointerDown { x, y, button: PointerButton::Primary });
                }
            } else if step.at.saturating_add(1) == frame {
                if let Some(key) = &step.press {
                    events.push(InputEvent::KeyUp { key: key.clone() });
                }
                if let Some(button) = step.button {
                    events.push(InputEvent::GamepadButton { button, pressed: false });
                }
                if let Some([x, y]) = step.click {
                    events.push(InputEvent::PointerUp { x, y, button: PointerButton::Primary });
                }
            }
        }
        events
    }
}

/// Runs `frames` frames at `width`×`height` with `script` and returns the last frame's pixels.
/// Only the last frame is rasterized.
pub fn run_frames(
    player: &mut Player,
    frames: u32,
    width: u32,
    height: u32,
    script: &InputScript,
) -> Result<Pixmap, PlayerError> {
    let frames = frames.max(1);
    for frame in 0..frames - 1 {
        player.step(FRAME_SECONDS, &script.events_at(frame), width, height)?;
    }
    let output = player.frame(FRAME_SECONDS, &script.events_at(frames - 1), width, height)?;
    Ok(output.pixels.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shorthand_steps_press_and_release() {
        let script = InputScript::parse(
            r#"{"steps":[{"at":2,"press":"enter"},{"at":2,"button":"south"},{"at":4,"click":[10,20]},
                {"at":5,"events":[{"type":"keyDown","key":"d"}]}]}"#,
        )
        .unwrap();
        assert!(script.events_at(0).is_empty());
        assert_eq!(
            script.events_at(2),
            [
                InputEvent::KeyDown { key: "enter".into(), repeat: false },
                InputEvent::GamepadButton { button: GamepadButton::South, pressed: true }
            ]
        );
        assert_eq!(script.events_at(3).len(), 2);
        assert_eq!(script.events_at(4).len(), 2);
        assert_eq!(script.events_at(5)[0], InputEvent::PointerUp { x: 10.0, y: 20.0, button: PointerButton::Primary });
        assert_eq!(script.events_at(5)[1], InputEvent::KeyDown { key: "d".into(), repeat: false });
        assert!(InputScript::parse(r#"{"steps":[{"at":1,"bogus":1}]}"#).is_err());
    }

    #[test]
    fn the_last_frame_index_does_not_overflow() {
        let script = InputScript::parse(r#"{"steps":[{"at":4294967295,"press":"enter"}]}"#).unwrap();
        assert!(script.events_at(0).is_empty());
        assert_eq!(script.events_at(u32::MAX), [InputEvent::KeyDown { key: "enter".into(), repeat: false }]);
    }
}
