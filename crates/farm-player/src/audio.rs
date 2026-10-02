//! Sound: the requests a frame produces and a small mixer that plays them.
//!
//! Every sound effect is a synthesized farm-runtime preset ([`SfxPreset::render`]), so games are
//! audible with zero assets. The player hands out [`SoundRequest`]s (cue + gain, the settings'
//! master × effects volume already applied); a host plays them however it likes. [`Mixer`] is the
//! platform-neutral part of the desktop player's audio thread: it sums the playing voices into an
//! interleaved output buffer. [`SoundBank`] renders every preset once, up front, so a real-time
//! audio callback only ever receives ready [`Voice`]s ([`Mixer::start`] neither allocates nor
//! frees). Music and ambience are the built-in loops of `farm_runtime::music`: a frame says which
//! should play ([`MusicCue`]), a [`MusicBank`] renders them once, and the mixer loops them in two
//! slots (music, ambience), fading between loops and volumes.

use farm_runtime::audio::{sfx_preset, SFX_PRESETS};
use farm_runtime::music::{self, MusicCue};
use std::collections::BTreeMap;
use std::sync::Arc;

/// A sound to play now.
#[derive(Debug, Clone, PartialEq)]
pub struct SoundRequest {
    /// A farm-runtime preset name (`ui`, `harvest`, `coin`, …).
    pub cue: String,
    /// Linear gain (0..=1), volumes applied.
    pub gain: f32,
}

/// A sound ready to mix: rendered samples (shared with a [`SoundBank`]) and a gain.
#[derive(Debug, Clone)]
pub struct Voice {
    samples: Arc<[f32]>,
    position: usize,
    gain: f32,
}

/// Every preset rendered at one sample rate, shared by the voices that play them. Keep the bank
/// alive while its voices play: a real-time callback then never frees the last reference.
#[derive(Debug, Clone)]
pub struct SoundBank {
    sample_rate: u32,
    sounds: BTreeMap<&'static str, Arc<[f32]>>,
}

impl SoundBank {
    /// Renders every farm-runtime preset at `sample_rate`.
    pub fn new(sample_rate: u32) -> Self {
        let sample_rate = sample_rate.max(1);
        let sounds = SFX_PRESETS.iter().map(|(name, preset)| (*name, Arc::from(preset.render(sample_rate)))).collect();
        Self { sample_rate, sounds }
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// The voice for `request`; `None` for unknown cues and gains that are not positive.
    pub fn voice(&self, request: &SoundRequest) -> Option<Voice> {
        // Also skips NaN gains.
        if request.gain.partial_cmp(&0.0) != Some(std::cmp::Ordering::Greater) {
            return None;
        }
        let samples = self.sounds.get(request.cue.as_str())?;
        Some(Voice { samples: Arc::clone(samples), position: 0, gain: request.gain.min(1.0) })
    }
}

/// Every built-in music and ambience loop rendered at one sample rate (several seconds of
/// rendering in all: build it off the audio callback, and off the frame loop where a hitch
/// shows). Keep it alive while the mixer loops its buffers.
#[derive(Debug, Clone)]
pub struct MusicBank {
    sample_rate: u32,
    loops: BTreeMap<&'static str, Arc<[f32]>>,
}

impl MusicBank {
    pub fn new(sample_rate: u32) -> Self {
        let sample_rate = sample_rate.max(1);
        let loops = music::MUSIC_LOOPS
            .iter()
            .chain(music::AMBIENCE_LOOPS)
            .filter_map(|name| music::render_loop(name, sample_rate).map(|samples| (*name, Arc::from(samples))))
            .collect();
        Self { sample_rate, loops }
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// The samples of a loop; `None` for an unknown name.
    pub fn get(&self, name: &str) -> Option<Arc<[f32]>> {
        self.loops.get(name).cloned()
    }

    /// The two loop slots' contents for `cue`: (music samples, gain), (ambience samples, gain).
    pub fn slots(&self, cue: &MusicCue) -> [LoopRequest; 2] {
        let slot = |name: Option<&str>, gain: f32| LoopRequest { samples: name.and_then(|name| self.get(name)), gain };
        [slot(cue.music, cue.music_gain), slot(cue.ambience, cue.ambience_gain)]
    }
}

/// What a loop slot should play: a loop (shared with a [`MusicBank`]) at a gain, or silence.
#[derive(Debug, Clone, Default)]
pub struct LoopRequest {
    pub samples: Option<Arc<[f32]>>,
    pub gain: f32,
}

/// A loop being played, with its gain moving toward a target.
#[derive(Debug, Clone)]
struct LoopVoice {
    samples: Arc<[f32]>,
    position: usize,
    gain: f32,
    target: f32,
}

/// One loop slot: what plays now and what is fading out.
#[derive(Debug, Clone, Default)]
struct LoopSlot {
    current: Option<LoopVoice>,
    fading: Option<LoopVoice>,
}

/// Loop slots: music and ambience.
pub const LOOP_SLOTS: usize = 2;
/// Seconds a fade between two loops (or two volumes) takes.
pub const LOOP_FADE_SECONDS: f32 = 1.2;

/// Mixes sound-effect voices (mono presets) and the two loop slots into interleaved output
/// frames.
#[derive(Debug, Clone)]
pub struct Mixer {
    sample_rate: u32,
    cache: BTreeMap<String, Arc<[f32]>>,
    voices: Vec<Voice>,
    loops: [LoopSlot; LOOP_SLOTS],
}

/// Voices mixed at once; the oldest is dropped beyond this.
pub const MAX_VOICES: usize = 16;

impl Mixer {
    pub fn new(sample_rate: u32) -> Self {
        // Room for every voice up front: the list must never grow in an audio callback.
        Self {
            sample_rate: sample_rate.max(1),
            cache: BTreeMap::new(),
            voices: Vec::with_capacity(MAX_VOICES + 1),
            loops: Default::default(),
        }
    }

    /// Plays `request` in loop slot `slot` (0 music, 1 ambience): the same loop moves to the new
    /// gain, another loop fades in while the old one fades out, and no loop fades the slot out.
    /// Never allocates, and frees a buffer only when the caller held no other reference to it
    /// (a [`MusicBank`] always does).
    pub fn set_loop(&mut self, slot: usize, request: LoopRequest) {
        let Some(slot) = self.loops.get_mut(slot) else { return };
        let gain = if request.gain.is_finite() { request.gain.clamp(0.0, 1.0) } else { 0.0 };
        let same = match (&slot.current, &request.samples) {
            (Some(current), Some(samples)) => Arc::ptr_eq(&current.samples, samples),
            (None, None) => true,
            _ => false,
        };
        if same {
            if let Some(current) = slot.current.as_mut() {
                current.target = gain;
            }
            return;
        }
        let was_fading = slot.fading.take();
        if let Some(mut old) = slot.current.take() {
            old.target = 0.0;
            slot.fading = Some(old);
        }
        slot.current = request.samples.filter(|samples| !samples.is_empty()).map(|samples| {
            // A loop that was fading out comes back from where it is.
            match was_fading.filter(|fading| Arc::ptr_eq(&fading.samples, &samples)) {
                Some(mut back) => {
                    back.target = gain;
                    back
                }
                None => LoopVoice { samples, position: 0, gain: 0.0, target: gain },
            }
        });
    }

    /// Whether a loop plays (or fades) in `slot`.
    pub fn loop_playing(&self, slot: usize) -> bool {
        self.loops.get(slot).is_some_and(|slot| slot.current.is_some() || slot.fading.is_some())
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// Starts a prepared voice (from a [`SoundBank`]). Never allocates: the oldest voice makes
    /// room beyond [`MAX_VOICES`].
    pub fn start(&mut self, voice: Voice) {
        if self.voices.len() >= MAX_VOICES {
            self.voices.remove(0);
        }
        self.voices.push(voice);
    }

    /// Starts a voice for `request` (unknown cues are ignored), rendering its preset on first use.
    /// Real-time callbacks use [`Mixer::start`] with a [`SoundBank`] instead.
    pub fn play(&mut self, request: &SoundRequest) {
        // Also skips NaN gains.
        if request.gain.partial_cmp(&0.0) != Some(std::cmp::Ordering::Greater) {
            return;
        }
        let samples = match self.cache.get(&request.cue) {
            Some(samples) => Arc::clone(samples),
            None => {
                let Some(preset) = sfx_preset(&request.cue) else { return };
                let samples: Arc<[f32]> = preset.render(self.sample_rate).into();
                self.cache.insert(request.cue.clone(), Arc::clone(&samples));
                samples
            }
        };
        self.start(Voice { samples, position: 0, gain: request.gain.min(1.0) });
    }

    /// Voices still playing.
    pub fn active(&self) -> usize {
        self.voices.len()
    }

    /// Fills `out` (interleaved, `channels` per frame) with the mix; silence when idle.
    pub fn mix(&mut self, out: &mut [f32], channels: usize) {
        out.fill(0.0);
        let channels = channels.max(1);
        let step = 1.0 / (LOOP_FADE_SECONDS * self.sample_rate as f32);
        for slot in &mut self.loops {
            for voice in [slot.current.as_mut(), slot.fading.as_mut()].into_iter().flatten() {
                for frame in out.chunks_exact_mut(channels) {
                    voice.gain = if voice.gain < voice.target {
                        (voice.gain + step).min(voice.target)
                    } else {
                        (voice.gain - step).max(voice.target)
                    };
                    let value = voice.samples[voice.position] * voice.gain;
                    for channel in frame {
                        *channel += value;
                    }
                    voice.position += 1;
                    if voice.position == voice.samples.len() {
                        voice.position = 0;
                    }
                }
            }
            if slot.fading.as_ref().is_some_and(|fading| fading.gain <= 0.0) {
                slot.fading = None;
            }
        }
        for voice in &mut self.voices {
            for frame in out.chunks_exact_mut(channels) {
                let Some(sample) = voice.samples.get(voice.position) else { break };
                let value = sample * voice.gain;
                for channel in frame {
                    *channel += value;
                }
                voice.position += 1;
            }
        }
        self.voices.retain(|voice| voice.position < voice.samples.len());
        for sample in out {
            *sample = sample.clamp(-1.0, 1.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mixes_presets_until_they_end() {
        let mut mixer = Mixer::new(8000);
        mixer.play(&SoundRequest { cue: "coin".into(), gain: 0.5 });
        mixer.play(&SoundRequest { cue: "no-such-cue".into(), gain: 1.0 });
        mixer.play(&SoundRequest { cue: "ui".into(), gain: 0.0 });
        assert_eq!(mixer.active(), 1);
        let mut out = vec![0.0f32; 2 * 256];
        mixer.mix(&mut out, 2);
        assert!(out.iter().any(|s| *s != 0.0));
        assert_eq!(out[0], out[1], "mono in both channels");
        assert!(out.iter().all(|s| s.abs() <= 0.5 + 1e-6));
        for _ in 0..20 {
            mixer.mix(&mut out, 2);
        }
        assert_eq!(mixer.active(), 0);
        mixer.mix(&mut out, 2);
        assert!(out.iter().all(|s| *s == 0.0));
    }

    #[test]
    fn loops_fade_in_repeat_and_cross_fade() {
        let rate = 1000;
        let up: Arc<[f32]> = Arc::from(vec![0.5f32; 300]);
        let down: Arc<[f32]> = Arc::from(vec![-0.25f32; 200]);
        let mut mixer = Mixer::new(rate);
        mixer.set_loop(0, LoopRequest { samples: Some(Arc::clone(&up)), gain: 1.0 });
        assert!(mixer.loop_playing(0) && !mixer.loop_playing(1));
        // Fades in over LOOP_FADE_SECONDS, then holds: past the loop's end it starts again.
        let mut out = vec![0.0f32; 2 * 2000];
        mixer.mix(&mut out, 2);
        assert!(out[0].abs() < 0.01, "starts silent: {}", out[0]);
        assert!((out[out.len() - 1] - 0.5).abs() < 1e-6, "full volume, looping: {}", out[out.len() - 1]);
        // Same loop, new volume: no restart, just a fade.
        mixer.set_loop(0, LoopRequest { samples: Some(Arc::clone(&up)), gain: 0.5 });
        mixer.mix(&mut out, 2);
        assert!((out[out.len() - 1] - 0.25).abs() < 1e-6);
        // Another loop: the old fades out while the new fades in, then only the new plays.
        mixer.set_loop(0, LoopRequest { samples: Some(Arc::clone(&down)), gain: 1.0 });
        mixer.mix(&mut out, 2);
        assert!((out[out.len() - 1] + 0.25).abs() < 1e-6, "{}", out[out.len() - 1]);
        // Silence fades the slot out and frees it.
        mixer.set_loop(0, LoopRequest::default());
        mixer.mix(&mut out, 2);
        mixer.mix(&mut out, 2);
        assert!(!mixer.loop_playing(0));
        assert!(out.iter().all(|s| *s == 0.0));
        // Unknown slots are ignored.
        mixer.set_loop(7, LoopRequest { samples: Some(up), gain: 1.0 });
    }

    #[test]
    fn the_music_bank_holds_every_loop() {
        let bank = MusicBank::new(4000);
        let cue = MusicCue::new(Some("day"), Some("rain"), 0.5);
        let [music, ambience] = bank.slots(&cue);
        assert!(music.samples.is_some() && ambience.samples.is_some());
        assert_eq!((music.gain, ambience.gain), (0.5, 0.4));
        assert!(bank.get("night").is_some() && bank.get("crickets").is_some() && bank.get("birds").is_some());
        let [silent, _] = bank.slots(&MusicCue::default());
        assert!(silent.samples.is_none());
    }

    #[test]
    fn banks_prepare_voices_up_front_and_mix_like_play() {
        let bank = SoundBank::new(8000);
        assert_eq!(bank.sample_rate(), 8000);
        assert!(bank.voice(&SoundRequest { cue: "no-such-cue".into(), gain: 1.0 }).is_none());
        assert!(bank.voice(&SoundRequest { cue: "coin".into(), gain: f32::NAN }).is_none());
        let request = SoundRequest { cue: "coin".into(), gain: 0.5 };
        let (mut played, mut started) = (Mixer::new(8000), Mixer::new(8000));
        played.play(&request);
        started.start(bank.voice(&request).unwrap());
        let (mut a, mut b) = (vec![0.0f32; 512], vec![0.0f32; 512]);
        played.mix(&mut a, 2);
        started.mix(&mut b, 2);
        assert_eq!(a, b);
        // Voices share the bank's samples, and the voice list never grows past its capacity.
        let capacity = started.voices.capacity();
        for _ in 0..3 * MAX_VOICES {
            started.start(bank.voice(&request).unwrap());
        }
        assert_eq!(started.active(), MAX_VOICES);
        assert_eq!(started.voices.capacity(), capacity);
        assert!(Arc::strong_count(&bank.sounds["coin"]) > MAX_VOICES);
    }
}
