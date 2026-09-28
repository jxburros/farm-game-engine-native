//! Sound: the requests a frame produces and a small mixer that plays them.
//!
//! Every sound effect is a synthesized farm-runtime preset ([`SfxPreset::render`]), so games are
//! audible with zero assets. The player hands out [`SoundRequest`]s (cue + gain, the settings'
//! master × effects volume already applied); a host plays them however it likes. [`Mixer`] is the
//! platform-neutral part of the desktop player's audio thread: it renders each preset once and
//! sums the playing voices into an interleaved output buffer. Music and ambience stay silent:
//! games have no music content yet.

use farm_runtime::audio::sfx_preset;
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

#[derive(Debug, Clone)]
struct Voice {
    samples: Arc<[f32]>,
    position: usize,
    gain: f32,
}

/// Mixes sound-effect voices (mono presets) into interleaved output frames.
#[derive(Debug, Clone)]
pub struct Mixer {
    sample_rate: u32,
    cache: BTreeMap<String, Arc<[f32]>>,
    voices: Vec<Voice>,
}

/// Voices mixed at once; the oldest is dropped beyond this.
pub const MAX_VOICES: usize = 16;

impl Mixer {
    pub fn new(sample_rate: u32) -> Self {
        Self { sample_rate: sample_rate.max(1), cache: BTreeMap::new(), voices: Vec::new() }
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// Starts a voice for `request` (unknown cues are ignored).
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
        if self.voices.len() >= MAX_VOICES {
            self.voices.remove(0);
        }
        self.voices.push(Voice { samples, position: 0, gain: request.gain.min(1.0) });
    }

    /// Voices still playing.
    pub fn active(&self) -> usize {
        self.voices.len()
    }

    /// Fills `out` (interleaved, `channels` per frame) with the mix; silence when idle.
    pub fn mix(&mut self, out: &mut [f32], channels: usize) {
        out.fill(0.0);
        let channels = channels.max(1);
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
}
