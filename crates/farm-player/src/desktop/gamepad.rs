//! Gamepads through gilrs, mapped onto the player's abstract buttons and axes.

use crate::input::{GamepadAxis, InputEvent};
use farm_ui::GamepadButton;
use gilrs::{Axis, Button, EventType, Gilrs};

/// Connected gamepads; `None` when the platform has no gamepad support.
pub struct Gamepads {
    gilrs: Gilrs,
}

impl std::fmt::Debug for Gamepads {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Gamepads").finish_non_exhaustive()
    }
}

fn button(button: Button) -> Option<GamepadButton> {
    Some(match button {
        Button::South => GamepadButton::South,
        Button::East => GamepadButton::East,
        Button::West => GamepadButton::West,
        Button::North => GamepadButton::North,
        Button::Start => GamepadButton::Start,
        Button::Select => GamepadButton::Select,
        Button::LeftTrigger => GamepadButton::LeftShoulder,
        Button::RightTrigger => GamepadButton::RightShoulder,
        Button::LeftTrigger2 => GamepadButton::LeftTrigger,
        Button::RightTrigger2 => GamepadButton::RightTrigger,
        Button::LeftThumb => GamepadButton::LeftStick,
        Button::RightThumb => GamepadButton::RightStick,
        Button::DPadUp => GamepadButton::DpadUp,
        Button::DPadDown => GamepadButton::DpadDown,
        Button::DPadLeft => GamepadButton::DpadLeft,
        Button::DPadRight => GamepadButton::DpadRight,
        _ => return None,
    })
}

/// gilrs sticks point up for +Y; the player's axes point down.
fn axis(axis: Axis, value: f32) -> Option<(GamepadAxis, f32)> {
    Some(match axis {
        Axis::LeftStickX => (GamepadAxis::LeftX, value),
        Axis::LeftStickY => (GamepadAxis::LeftY, -value),
        Axis::RightStickX => (GamepadAxis::RightX, value),
        Axis::RightStickY => (GamepadAxis::RightY, -value),
        _ => return None,
    })
}

impl Gamepads {
    pub fn start() -> Option<Self> {
        Gilrs::new().ok().map(|gilrs| Self { gilrs })
    }

    /// Events since the last poll.
    pub fn poll(&mut self, out: &mut Vec<InputEvent>) {
        while let Some(event) = self.gilrs.next_event() {
            match event.event {
                EventType::ButtonPressed(pressed, _) => {
                    if let Some(button) = button(pressed) {
                        out.push(InputEvent::GamepadButton { button, pressed: true });
                    }
                }
                EventType::ButtonReleased(released, _) => {
                    if let Some(button) = button(released) {
                        out.push(InputEvent::GamepadButton { button, pressed: false });
                    }
                }
                EventType::AxisChanged(changed, value, _) => {
                    if let Some((axis, value)) = axis(changed, value) {
                        out.push(InputEvent::GamepadAxis { axis, value });
                    }
                }
                EventType::Disconnected => {
                    for button in GamepadButton::ALL {
                        out.push(InputEvent::GamepadButton { button, pressed: false });
                    }
                    for axis in [GamepadAxis::LeftX, GamepadAxis::LeftY, GamepadAxis::RightX, GamepadAxis::RightY] {
                        out.push(InputEvent::GamepadAxis { axis, value: 0.0 });
                    }
                }
                _ => {}
            }
        }
    }
}
