//! Port of `Runtime/FixedTimestepAndAudioTests.cs`.

use farm_runtime::audio::{
    self, AudioBackend, AudioManager, AudioSettings, AudioSettingsPatch, InMemoryKeyValueStore, KeyValueStore,
    SfxPreset, DEFAULT_SETTINGS,
};
use farm_runtime::timestep::FixedTimestep;
use farm_sim::{js, Effect};
use std::cell::RefCell;
use std::rc::Rc;

/// C# `Assert.Equal(expected, actual, 9)`.
fn assert_close(expected: f64, actual: f64) {
    assert!((expected - actual).abs() < 5e-10, "expected {expected}, got {actual}");
}

#[test]
fn fixed_timestep_accumulates_whole_ticks_and_exposes_alpha() {
    let mut timestep = FixedTimestep::new();
    assert_eq!(timestep.advance(0.02), 0);
    assert_close(0.4, timestep.alpha());
    assert_eq!(timestep.advance(0.04), 1);
    assert_close(0.2, timestep.alpha());
    // Frame deltas are capped at 250 ms (5 ticks at 20 t/s).
    assert_eq!(timestep.advance(10.0), 5);
    timestep.reset();
    assert_eq!(timestep.alpha(), 0.0);
}

#[derive(Default)]
struct RecordingBackend {
    calls: Rc<RefCell<Vec<String>>>,
}

impl RecordingBackend {
    fn record(&self, call: String) {
        self.calls.borrow_mut().push(call);
    }
}

impl AudioBackend for RecordingBackend {
    fn set_volumes(&mut self, master: f64, sfx: f64, music: f64) {
        self.record(format!("volumes {} {} {}", js::num(master), js::num(sfx), js::num(music)));
    }
    fn play_tone(&mut self, name: &str, _preset: &SfxPreset) {
        self.record(format!("tone {name}"));
    }
    fn play_music(&mut self, url: Option<&str>) {
        self.record(format!("music {}", url.unwrap_or("")));
    }
    fn suspend(&mut self) {
        self.record("suspend".to_owned());
    }
    fn resume(&mut self) {
        self.record("resume".to_owned());
    }
    fn dispose(&mut self) {
        self.record("dispose".to_owned());
    }
}

#[test]
fn audio_settings_persist_through_the_injected_store() {
    let store = InMemoryKeyValueStore::new();
    let mut audio = AudioManager::new(Some(Box::new(store.clone())), None);
    assert_eq!(audio.settings(), DEFAULT_SETTINGS);
    audio.configure(AudioSettingsPatch { master: Some(0.5), muted: Some(true), ..AudioSettingsPatch::default() });
    assert_eq!(
        store.get_item(AudioManager::SETTINGS_KEY).as_deref(),
        Some("{\"master\":0.5,\"sfx\":0.9,\"music\":0.6,\"muted\":true}")
    );

    let reloaded = AudioManager::new(Some(Box::new(store.clone())), None);
    assert_eq!(reloaded.settings().master, 0.5);
    assert!(reloaded.settings().muted);
}

#[test]
fn corrupt_or_partial_settings_fall_back_to_defaults() {
    let mut store = InMemoryKeyValueStore::new();
    store.set_item(AudioManager::SETTINGS_KEY, "{not json");
    assert_eq!(AudioManager::new(Some(Box::new(store.clone())), None).settings(), DEFAULT_SETTINGS);
    store.set_item(AudioManager::SETTINGS_KEY, "{\"sfx\":0.1}");
    assert_eq!(
        AudioManager::new(Some(Box::new(store.clone())), None).settings(),
        AudioSettings { sfx: 0.1, ..DEFAULT_SETTINGS }
    );
}

#[test]
fn plays_presets_only_after_unlock_and_while_unmuted() {
    let backend = RecordingBackend::default();
    let calls = Rc::clone(&backend.calls);
    let mut audio = AudioManager::new(None, Some(Box::new(backend)));
    audio.play("coin");
    assert!(calls.borrow().is_empty());

    audio.unlock();
    audio.play("coin");
    audio.play("no-such-sound");
    audio.set_visible(false);
    audio.set_visible(true);
    audio.configure(AudioSettingsPatch { muted: Some(true), ..AudioSettingsPatch::default() });
    audio.play("coin");
    assert_eq!(
        *calls.borrow(),
        ["volumes 0.8 0.9 0.6", "resume", "tone coin", "suspend", "resume", "volumes 0 0.9 0.6"]
    );
}

#[test]
fn maps_effects_to_sfx() {
    let message = |level: &str| Effect::message(level, "x");
    assert_eq!(audio::sfx_for_effect(&Effect::Sound { id: "chop".to_owned() }), Some("chop"));
    assert_eq!(audio::sfx_for_effect(&message("error")), Some("error"));
    assert_eq!(audio::sfx_for_effect(&message("success")), Some("success"));
    assert_eq!(audio::sfx_for_effect(&message("info")), None);
    assert_eq!(
        audio::sfx_for_effect(&Effect::CropHarvested { crop_type: "wheat".to_owned(), quantity: 1.0 }),
        Some("harvest")
    );
    assert_eq!(audio::sfx_for_effect(&Effect::QuestCompleted { quest_id: "q".to_owned() }), Some("quest"));
    assert_eq!(
        audio::sfx_for_effect(&Effect::DayStarted { day: 2.0, season: "spring".to_owned(), year: 1.0 }),
        Some("sleep")
    );
    assert_eq!(audio::sfx_for_effect(&Effect::SceneChanged { scene_id: "s".to_owned(), x: 0.0, y: 0.0 }), Some("ui"));
    assert_eq!(audio::sfx_for_effect(&Effect::PlayerMoved { x: 0.0, y: 0.0 }), None);
}

#[test]
fn presets_render_to_bounded_pcm() {
    let preset = audio::sfx_preset("quest").unwrap();
    let samples = preset.render(8000);
    assert_eq!(samples.len(), (preset.total_duration() * 8000.0).ceil() as usize);
    assert!(samples.iter().all(|s| (-0.31..=0.31).contains(s)));
    assert!(samples.iter().any(|s| s.abs() > 0.1));
}
