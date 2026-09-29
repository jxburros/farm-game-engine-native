//! The audio model (port of `Audio.cs` / engine-runtime `audio.ts`): persisted volume settings,
//! unlock/mute/visibility semantics, the synthesized SFX presets and the effect → sound cue
//! mapping. Playback is not here: [`AudioManager`] hands it to a host-provided [`AudioBackend`]
//! (the player's audio device), so everything in this module is pure and headless-safe.
//!
//! Games are audible with zero assets — every SFX is a synthesized tone description (the audio
//! analogue of the rectangle fallback), which [`SfxPreset::render`] turns into PCM for backends
//! that just play buffers.

use farm_sim::effects::message_levels;
use farm_sim::{units, Effect};
use indexmap::IndexMap;
use serde_json::Value;
use std::borrow::Cow;
use std::cell::RefCell;
use std::f64::consts::PI;
use std::fmt;
use std::rc::Rc;

/// Persisted volume settings (TS `AudioSettings`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AudioSettings {
    pub master: f64,
    pub sfx: f64,
    pub music: f64,
    pub muted: bool,
}

/// TS `DEFAULT_SETTINGS`.
pub const DEFAULT_SETTINGS: AudioSettings = AudioSettings { master: 0.8, sfx: 0.9, music: 0.6, muted: false };

impl Default for AudioSettings {
    fn default() -> Self {
        DEFAULT_SETTINGS
    }
}

impl AudioSettings {
    /// The persisted JSON (`{"master":…,"sfx":…,"music":…,"muted":…}`, JS number formatting).
    /// `None` when a volume is not finite: the C# serializer (strict number handling) throws
    /// there, and the settings are simply not persisted.
    pub fn to_json(&self) -> Option<String> {
        if ![self.master, self.sfx, self.music].iter().all(|v| v.is_finite()) {
            return None;
        }
        Some(format!(
            "{{\"master\":{},\"sfx\":{},\"music\":{},\"muted\":{}}}",
            units::format_number(self.master),
            units::format_number(self.sfx),
            units::format_number(self.music),
            self.muted
        ))
    }

    /// `{ ...defaults, ...JSON.parse(raw) }` over the known keys, type-checked (C#
    /// `MergeSettings`); `None` when `raw` is not valid JSON (corrupt settings → the caller uses
    /// the defaults). A non-object JSON value yields `defaults`.
    pub fn merge_json(defaults: AudioSettings, raw: &str) -> Option<AudioSettings> {
        let parsed: Value = serde_json::from_str(raw).ok()?;
        let Value::Object(obj) = parsed else { return Some(defaults) };
        let num = |key: &str, fallback: f64| obj.get(key).and_then(Value::as_f64).unwrap_or(fallback);
        let flag = |key: &str, fallback: bool| obj.get(key).and_then(Value::as_bool).unwrap_or(fallback);
        Some(AudioSettings {
            master: num("master", defaults.master),
            sfx: num("sfx", defaults.sfx),
            music: num("music", defaults.music),
            muted: flag("muted", defaults.muted),
        })
    }
}

/// A partial settings update (TS `configure(patch)`; the C# optional arguments).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct AudioSettingsPatch {
    pub master: Option<f64>,
    pub sfx: Option<f64>,
    pub music: Option<f64>,
    pub muted: Option<bool>,
}

/// Oscillator waveforms (WebAudio `OscillatorType` names).
pub mod waveforms {
    pub const SINE: &str = "sine";
    pub const SQUARE: &str = "square";
    pub const TRIANGLE: &str = "triangle";
    pub const SAWTOOTH: &str = "sawtooth";
}

/// A synthesized SFX: one oscillator stepping through `steps` (Hz, each held
/// `duration / steps.len()` seconds), with a gain envelope starting at `gain` and ramping
/// exponentially to 0.001 over `duration`; the oscillator stops 20 ms after that.
#[derive(Debug, Clone, PartialEq)]
pub struct SfxPreset {
    pub steps: Cow<'static, [f64]>,
    /// Seconds.
    pub duration: f64,
    /// One of [`waveforms`] (anything else plays as a sine).
    pub waveform: Cow<'static, str>,
    pub gain: f64,
}

impl SfxPreset {
    const fn fixed(steps: &'static [f64], duration: f64, waveform: &'static str, gain: f64) -> Self {
        Self { steps: Cow::Borrowed(steps), duration, waveform: Cow::Borrowed(waveform), gain }
    }

    /// Total sounding time in seconds (TS `oscillator.stop(now + duration + 0.02)`).
    pub fn total_duration(&self) -> f64 {
        self.duration + 0.02
    }

    /// Render mono PCM samples in [-1, 1] at `sample_rate`, before the SFX/master volumes (the
    /// backend applies those). Floating point here only shapes sound; it never feeds the
    /// simulation.
    pub fn render(&self, sample_rate: u32) -> Vec<f32> {
        let rate = f64::from(sample_rate);
        let count = (self.total_duration() * rate).ceil() as usize;
        let step_count = self.steps.len();
        let step_length = self.duration / step_count as f64;
        let mut samples = Vec::with_capacity(count);
        let mut phase = 0.0_f64;
        for i in 0..count {
            let t = i as f64 / rate;
            let step = ((t / step_length).floor() as usize).min(step_count - 1);
            phase += self.steps[step] / rate;
            phase -= phase.floor();
            let wave = match &*self.waveform {
                waveforms::SQUARE => {
                    if phase < 0.5 {
                        1.0
                    } else {
                        -1.0
                    }
                }
                waveforms::TRIANGLE => 1.0 - 4.0 * (phase - 0.5).abs(),
                waveforms::SAWTOOTH => 2.0 * phase - 1.0,
                _ => (2.0 * PI * phase).sin(),
            };
            // exponentialRampToValueAtTime(0.001, duration), then held.
            let envelope =
                if t >= self.duration { 0.001 } else { self.gain * (0.001 / self.gain).powf(t / self.duration) };
            samples.push((wave * envelope) as f32);
        }
        samples
    }
}

/// name → tone description (TS `SFX_PRESETS`), in declaration order.
pub static SFX_PRESETS: &[(&str, SfxPreset)] = &[
    ("ui", SfxPreset::fixed(&[660.0], 0.06, waveforms::SINE, 0.25)),
    ("success", SfxPreset::fixed(&[523.0, 659.0], 0.12, waveforms::TRIANGLE, 0.3)),
    ("error", SfxPreset::fixed(&[196.0, 147.0], 0.16, waveforms::SQUARE, 0.18)),
    ("till", SfxPreset::fixed(&[140.0, 100.0], 0.1, waveforms::TRIANGLE, 0.35)),
    ("water", SfxPreset::fixed(&[420.0, 340.0, 300.0], 0.18, waveforms::SINE, 0.25)),
    ("chop", SfxPreset::fixed(&[180.0, 90.0], 0.09, waveforms::SQUARE, 0.28)),
    ("harvest", SfxPreset::fixed(&[392.0, 523.0, 659.0], 0.2, waveforms::TRIANGLE, 0.32)),
    ("coin", SfxPreset::fixed(&[988.0, 1319.0], 0.11, waveforms::SQUARE, 0.2)),
    ("quest", SfxPreset::fixed(&[523.0, 659.0, 784.0, 1047.0], 0.4, waveforms::TRIANGLE, 0.3)),
    ("sleep", SfxPreset::fixed(&[330.0, 262.0, 196.0], 0.5, waveforms::SINE, 0.25)),
    ("step", SfxPreset::fixed(&[220.0], 0.03, waveforms::TRIANGLE, 0.08)),
];

/// The preset registered under `name` (C# `Audio.SfxPresets[name]`).
pub fn sfx_preset(name: &str) -> Option<&'static SfxPreset> {
    SFX_PRESETS.iter().find(|(preset_name, _)| *preset_name == name).map(|(_, preset)| preset)
}

/// Map an engine effect to an SFX preset name (data-driven hook for hosts; C#
/// `Audio.SfxForEffect`).
pub fn sfx_for_effect(effect: &Effect) -> Option<&str> {
    match effect {
        Effect::Sound { id } => Some(id),
        Effect::Message { level, .. } => match level.as_str() {
            message_levels::ERROR => Some("error"),
            message_levels::SUCCESS => Some("success"),
            _ => None,
        },
        Effect::CropHarvested { .. } => Some("harvest"),
        Effect::QuestCompleted { .. } => Some("quest"),
        Effect::DayStarted { .. } => Some("sleep"),
        Effect::SceneChanged { .. } => Some("ui"),
        Effect::PlayerMoved { .. } => None,
    }
}

/// Minimal key-value persistence (the TS code's `localStorage`; C# `IKeyValueStore`).
/// Implementations must not panic: a failing store just doesn't persist.
pub trait KeyValueStore {
    fn get_item(&self, key: &str) -> Option<String>;
    fn set_item(&mut self, key: &str, value: &str);
}

/// Process-lifetime store (tests, or hosts without persistence). Clones share the same map, so
/// a host (or test) can keep a handle to what an [`AudioManager`] persisted.
#[derive(Debug, Clone, Default)]
pub struct InMemoryKeyValueStore {
    items: Rc<RefCell<IndexMap<String, String>>>,
}

impl InMemoryKeyValueStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl KeyValueStore for InMemoryKeyValueStore {
    fn get_item(&self, key: &str) -> Option<String> {
        self.items.borrow().get(key).cloned()
    }

    fn set_item(&mut self, key: &str, value: &str) {
        self.items.borrow_mut().insert(key.to_owned(), value.to_owned());
    }
}

/// The host's audio output (C# `IAudioBackend`). All calls come from the game loop thread;
/// implementations should never panic (audio failures must not break play).
pub trait AudioBackend {
    /// Apply bus volumes (master is 0 while muted).
    fn set_volumes(&mut self, master: f64, sfx: f64, music: f64);
    /// Play a synthesized effect on the SFX bus.
    fn play_tone(&mut self, name: &str, preset: &SfxPreset);
    /// Loop music from a URL/path (data: URLs from packs too); `None` stops music. Crossfade is
    /// optional.
    fn play_music(&mut self, url: Option<&str>);
    /// Pause all output (window hidden / minimized).
    fn suspend(&mut self);
    /// Resume output.
    fn resume(&mut self);
    /// Release the output device (C# `IDisposable.Dispose`); the backend is dropped afterwards.
    fn dispose(&mut self) {}
}

/// Audio manager (M7): synthesized SFX presets, optional music, persisted volume settings,
/// mute-on-blur. All methods are safe to call headless (no backend → no-ops). Call
/// [`unlock`](Self::unlock) once the host is ready to make sound (the browser version waits for
/// a user gesture).
pub struct AudioManager {
    storage: Option<Box<dyn KeyValueStore>>,
    backend: Option<Box<dyn AudioBackend>>,
    unlocked: bool,
    settings: AudioSettings,
}

impl AudioManager {
    /// The `localStorage` key the settings persist under.
    pub const SETTINGS_KEY: &'static str = "farm-audio-settings";

    pub fn new(storage: Option<Box<dyn KeyValueStore>>, backend: Option<Box<dyn AudioBackend>>) -> Self {
        let settings = storage
            .as_ref()
            .and_then(|store| store.get_item(Self::SETTINGS_KEY))
            .filter(|raw| !raw.is_empty())
            // Corrupt settings → defaults.
            .and_then(|raw| AudioSettings::merge_json(DEFAULT_SETTINGS, &raw))
            .unwrap_or(DEFAULT_SETTINGS);
        Self { storage, backend, unlocked: false, settings }
    }

    pub fn settings(&self) -> AudioSettings {
        self.settings
    }

    pub fn is_unlocked(&self) -> bool {
        self.unlocked
    }

    /// Attach/replace the output backend (e.g. once the platform audio device opens).
    pub fn attach_backend(&mut self, backend: Option<Box<dyn AudioBackend>>) {
        self.backend = backend;
        if self.unlocked {
            self.apply_volumes();
        }
    }

    /// Start/resume output (TS: create/resume the AudioContext on a user gesture).
    pub fn unlock(&mut self) {
        if self.backend.is_none() {
            return;
        }
        if !self.unlocked {
            self.unlocked = true;
            self.apply_volumes();
        }
        if let Some(backend) = self.backend.as_mut() {
            backend.resume();
        }
    }

    fn apply_volumes(&mut self) {
        let AudioSettings { master, sfx, music, muted } = self.settings;
        if let Some(backend) = self.backend.as_mut() {
            backend.set_volumes(if muted { 0.0 } else { master }, sfx, music);
        }
    }

    /// Patch settings (TS `configure(patch)`), apply and persist them.
    pub fn configure(&mut self, patch: AudioSettingsPatch) {
        let current = self.settings;
        self.settings = AudioSettings {
            master: patch.master.unwrap_or(current.master),
            sfx: patch.sfx.unwrap_or(current.sfx),
            music: patch.music.unwrap_or(current.music),
            muted: patch.muted.unwrap_or(current.muted),
        };
        if self.unlocked {
            self.apply_volumes();
        }
        // Unserializable settings (non-finite volumes) just don't persist.
        if let (Some(store), Some(json)) = (self.storage.as_mut(), self.settings.to_json()) {
            store.set_item(Self::SETTINGS_KEY, &json);
        }
    }

    /// Play a named synthesized effect. Unknown names are ignored; silent until unlocked or
    /// while muted.
    pub fn play(&mut self, name: &str) {
        let Some(preset) = sfx_preset(name) else { return };
        if !self.unlocked || self.settings.muted {
            return;
        }
        if let Some(backend) = self.backend.as_mut() {
            backend.play_tone(name, preset);
        }
    }

    /// Loop music from a URL (data: URLs from packs work too); `None` stops it.
    pub fn play_music(&mut self, url: Option<&str>) {
        if !self.unlocked {
            return;
        }
        if let Some(backend) = self.backend.as_mut() {
            backend.play_music(url);
        }
    }

    /// Mute-on-blur: suspend while the window is hidden, resume (unless muted) when shown.
    pub fn set_visible(&mut self, visible: bool) {
        if !self.unlocked {
            return;
        }
        let muted = self.settings.muted;
        let Some(backend) = self.backend.as_mut() else { return };
        if !visible {
            backend.suspend();
        } else if !muted {
            backend.resume();
        }
    }

    /// Stop music, release the backend and lock again (C# `Dispose`).
    pub fn dispose(&mut self) {
        if let Some(mut backend) = self.backend.take() {
            backend.play_music(None);
            backend.dispose();
        }
        self.unlocked = false;
    }
}

impl fmt::Debug for AudioManager {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("AudioManager")
            .field("has_storage", &self.storage.is_some())
            .field("has_backend", &self.backend.is_some())
            .field("unlocked", &self.unlocked)
            .field("settings", &self.settings)
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_json_uses_javascript_number_formatting() {
        let settings = AudioSettings { master: 1.0, ..DEFAULT_SETTINGS };
        assert_eq!(settings.to_json().as_deref(), Some(r#"{"master":1,"sfx":0.9,"music":0.6,"muted":false}"#));
        assert_eq!(AudioSettings { sfx: f64::NAN, ..DEFAULT_SETTINGS }.to_json(), None);
    }

    #[test]
    fn merge_ignores_wrongly_typed_keys_and_non_objects() {
        let merged = AudioSettings::merge_json(DEFAULT_SETTINGS, r#"{"master":"loud","muted":1,"music":0.2}"#);
        assert_eq!(merged, Some(AudioSettings { music: 0.2, ..DEFAULT_SETTINGS }));
        assert_eq!(AudioSettings::merge_json(DEFAULT_SETTINGS, "[1]"), Some(DEFAULT_SETTINGS));
        assert_eq!(AudioSettings::merge_json(DEFAULT_SETTINGS, "{"), None);
    }

    #[derive(Default)]
    struct Calls(Rc<RefCell<Vec<&'static str>>>);

    impl AudioBackend for Calls {
        fn set_volumes(&mut self, _: f64, _: f64, _: f64) {
            self.0.borrow_mut().push("volumes");
        }
        fn play_tone(&mut self, _: &str, _: &SfxPreset) {
            self.0.borrow_mut().push("tone");
        }
        fn play_music(&mut self, url: Option<&str>) {
            self.0.borrow_mut().push(if url.is_some() { "music" } else { "music-stop" });
        }
        fn suspend(&mut self) {
            self.0.borrow_mut().push("suspend");
        }
        fn resume(&mut self) {
            self.0.borrow_mut().push("resume");
        }
        fn dispose(&mut self) {
            self.0.borrow_mut().push("dispose");
        }
    }

    #[test]
    fn dispose_stops_music_and_releases_the_backend() {
        let backend = Calls::default();
        let calls = Rc::clone(&backend.0);
        let mut audio = AudioManager::new(None, Some(Box::new(backend)));
        audio.play_music(Some("song.ogg")); // locked: ignored
        audio.unlock();
        audio.play_music(Some("song.ogg"));
        audio.dispose();
        assert!(!audio.is_unlocked());
        audio.play("coin");
        audio.unlock(); // no backend any more: stays locked
        assert!(!audio.is_unlocked());
        assert_eq!(*calls.borrow(), ["volumes", "resume", "music", "music-stop", "dispose"]);
    }

    #[test]
    fn every_preset_renders_within_its_gain() {
        for (name, preset) in SFX_PRESETS {
            let samples = preset.render(22050);
            assert!(!samples.is_empty(), "{name}");
            let limit = preset.gain as f32 + 1e-6;
            assert!(samples.iter().all(|s| s.abs() <= limit), "{name}");
        }
    }
}
