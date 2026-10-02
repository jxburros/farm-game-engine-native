//! Built-in music and ambience: seamless loops synthesized from code, the way the SFX presets
//! are, so every game has a soundtrack with zero assets and nothing third-party to license.
//!
//! - **Music**: `day` (a bright pentatonic tune over I–vi–IV–V in C, 92 BPM) and `night` (a
//!   slow bell melody in A minor, 66 BPM). Each is a short score (chords, bass and a melody
//!   drawn from a fixed seed) played by small synthesized instruments.
//! - **Ambience**: `birds` (wind and chirps), `crickets` (night chirps) and `rain` (filtered
//!   noise and drops).
//!
//! [`render_loop`] renders a loop to mono PCM in [-1, 1] at any sample rate; the end blends into
//! the start, so it repeats without a click. Rendering is deterministic (fixed seeds, no OS
//! randomness) and only shapes sound: nothing here reaches the simulation. [`MusicCue`] is what a
//! player wants playing now; [`cue_for`] picks it from the game state.

use farm_sim::schema::{GameContent, GameState};
use farm_sim::units;
use std::f64::consts::TAU;

pub const MUSIC_DAY: &str = "day";
pub const MUSIC_NIGHT: &str = "night";
pub const AMBIENCE_BIRDS: &str = "birds";
pub const AMBIENCE_CRICKETS: &str = "crickets";
pub const AMBIENCE_RAIN: &str = "rain";

/// Every music loop, then every ambience loop.
pub const MUSIC_LOOPS: &[&str] = &[MUSIC_DAY, MUSIC_NIGHT];
pub const AMBIENCE_LOOPS: &[&str] = &[AMBIENCE_BIRDS, AMBIENCE_CRICKETS, AMBIENCE_RAIN];

/// What should be playing: a music loop and an ambience loop (`None` for silence), with their
/// gains (volumes applied, 0..=1). Hosts fade between cues.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MusicCue {
    pub music: Option<&'static str>,
    pub music_gain: f32,
    pub ambience: Option<&'static str>,
    pub ambience_gain: f32,
}

/// Ambience plays a little under the music.
const AMBIENCE_LEVEL: f32 = 0.8;

impl MusicCue {
    /// The cue for `music` and `ambience` at the music volume `gain` (master × music, 0 when
    /// muted). A zero gain plays nothing.
    pub fn new(music: Option<&'static str>, ambience: Option<&'static str>, gain: f32) -> Self {
        let gain = if gain.is_finite() { gain.clamp(0.0, 1.0) } else { 0.0 };
        if gain <= 0.0 {
            return Self::default();
        }
        Self { music, music_gain: gain, ambience, ambience_gain: gain * AMBIENCE_LEVEL }
    }
}

/// The loops for a moment of play: night music and crickets from 19:00 to 6:00, else day music
/// and birds; rain ambience under rain (a weather whose overlay is `rain`); no ambience in an
/// indoor scene.
pub fn cue_for(content: &GameContent, state: &GameState, gain: f32) -> MusicCue {
    let hour = state.clock.time_minutes / units::MINUTE / 60 % 24;
    let night = !(6..19).contains(&hour);
    let music = if night { MUSIC_NIGHT } else { MUSIC_DAY };
    let indoor =
        state.world.scenes.iter().find(|scene| scene.id == state.player.scene_id).is_some_and(|s| s.is_indoor());
    let raining = content
        .weather
        .types
        .iter()
        .find(|weather| weather.id == state.clock.weather_id)
        .is_some_and(|weather| weather.overlay.as_deref() == Some("rain"));
    let ambience = if indoor {
        None
    } else if raining {
        Some(AMBIENCE_RAIN)
    } else if night {
        Some(AMBIENCE_CRICKETS)
    } else {
        Some(AMBIENCE_BIRDS)
    };
    MusicCue::new(Some(music), ambience, gain)
}

/// The loop `name` (one of [`MUSIC_LOOPS`] or [`AMBIENCE_LOOPS`]) as mono samples at
/// `sample_rate`; `None` for an unknown name.
pub fn render_loop(name: &str, sample_rate: u32) -> Option<Vec<f32>> {
    let rate = f64::from(sample_rate.max(1000));
    let samples = match name {
        MUSIC_DAY => day(rate),
        MUSIC_NIGHT => night(rate),
        AMBIENCE_BIRDS => birds(rate),
        AMBIENCE_CRICKETS => crickets(rate),
        AMBIENCE_RAIN => rain(rate),
        _ => return None,
    };
    Some(samples)
}

// ---- building blocks ----

/// A small deterministic noise source (xorshift32).
struct Noise(u32);

impl Noise {
    fn next(&mut self) -> f64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 17;
        self.0 ^= self.0 << 5;
        f64::from(self.0) / f64::from(u32::MAX)
    }

    /// White noise in [-1, 1].
    fn white(&mut self) -> f64 {
        self.next() * 2.0 - 1.0
    }
}

/// A loop being rendered: `length` samples plus a tail that wraps around to the start.
struct Track {
    rate: f64,
    samples: Vec<f64>,
}

impl Track {
    fn new(seconds: f64, rate: f64) -> Self {
        Self { rate, samples: vec![0.0; (seconds * rate).round() as usize] }
    }

    /// Adds `value` at `index`, wrapping past the end (a note's tail continues at the start).
    fn add(&mut self, index: usize, value: f64) {
        let len = self.samples.len();
        self.samples[index % len] += value;
    }

    /// Adds a voice from `start` seconds for `seconds`: `wave(t)` × `envelope(t)` × `gain`.
    fn voice(&mut self, start: f64, seconds: f64, gain: f64, sound: impl Fn(f64) -> f64) {
        let first = (start * self.rate).round() as usize;
        let count = (seconds * self.rate).round() as usize;
        for i in 0..count {
            let t = i as f64 / self.rate;
            self.add(first + i, gain * sound(t));
        }
    }

    /// The finished loop: peak-normalized to `peak`, as f32.
    fn finish(self, peak: f64) -> Vec<f32> {
        let max = self.samples.iter().fold(0.0_f64, |max, sample| max.max(sample.abs()));
        let scale = if max > 0.0 { peak / max } else { 0.0 };
        self.samples.iter().map(|sample| (sample * scale) as f32).collect()
    }
}

/// Frequency of a MIDI note.
fn hz(note: i32) -> f64 {
    440.0 * 2f64.powf(f64::from(note - 69) / 12.0)
}

fn triangle(phase: f64) -> f64 {
    1.0 - 4.0 * ((phase - phase.floor()) - 0.5).abs()
}

/// Attack, then a hold, then a release to silence at `seconds`.
fn swell(t: f64, seconds: f64, attack: f64, release: f64) -> f64 {
    let rise = (t / attack).min(1.0);
    let fall = ((seconds - t) / release).clamp(0.0, 1.0);
    rise * fall
}

/// A plucked envelope: a short attack, then an exponential decay with time constant `decay`.
fn pluck(t: f64, decay: f64) -> f64 {
    (t / 0.006).min(1.0) * (-t / decay).exp()
}

/// A soft pad note (two detuned triangles).
fn pad(track: &mut Track, start: f64, seconds: f64, note: i32, gain: f64) {
    let f = hz(note);
    track.voice(start, seconds + 0.6, gain, |t| {
        let wave = 0.5 * triangle(t * f) + 0.5 * triangle(t * f * 1.004);
        wave * swell(t, seconds + 0.6, 0.35, 0.8)
    });
}

/// A round bass note.
fn bass(track: &mut Track, start: f64, seconds: f64, note: i32, gain: f64) {
    let f = hz(note);
    track.voice(start, seconds, gain, |t| {
        let wave = (TAU * f * t).sin() + 0.25 * (TAU * 2.0 * f * t).sin();
        wave * pluck(t, 0.45) * swell(t, seconds, 0.005, 0.05)
    });
}

/// A plucked lead note with a little vibrato.
fn lead(track: &mut Track, start: f64, seconds: f64, note: i32, gain: f64) {
    let f = hz(note);
    let length = seconds + 0.4;
    track.voice(start, length, gain, |t| {
        let vibrato = 1.0 + 0.003 * (TAU * 5.0 * t).sin() * (t / 0.3).min(1.0);
        let phase = t * f * vibrato;
        (0.7 * triangle(phase) + 0.3 * (TAU * 2.0 * phase).sin()) * pluck(t, 0.5) * swell(t, length, 0.006, 0.08)
    });
}

/// A bell (inharmonic partials, a long decay).
fn bell(track: &mut Track, start: f64, note: i32, gain: f64) {
    let f = hz(note);
    track.voice(start, 2.5, gain, |t| {
        let wave = (TAU * f * t).sin()
            + 0.4 * (TAU * 2.76 * f * t).sin() * (-t / 0.4).exp()
            + 0.2 * (TAU * 5.4 * f * t).sin() * (-t / 0.2).exp();
        wave * pluck(t, 0.9) * swell(t, 2.5, 0.003, 0.3)
    });
}

/// A melody line: per beat, a note of `scale` near the last one, leaning on chord tones, with a
/// rest now and then. Fixed seed: the same tune every time.
fn melody(seed: u32, beats: usize, scale: &[i32], chord_at: impl Fn(usize) -> [i32; 3]) -> Vec<(f64, f64, i32)> {
    let mut noise = Noise(seed);
    let mut notes = Vec::new();
    let mut index = scale.len() / 2;
    let mut beat = 0.0;
    while beat < beats as f64 {
        let bar_beat = beat as usize;
        let choice = noise.next();
        let length: f64 = if choice < 0.45 {
            1.0
        } else if choice < 0.75 {
            0.5
        } else if choice < 0.92 {
            2.0
        } else {
            1.5
        };
        // Rests breathe at the end of every other bar.
        let rest = bar_beat % 8 == 7 || noise.next() < 0.12;
        if !rest {
            let step = (noise.next() * 5.0).floor() as i32 - 2;
            index = (index as i32 + step).clamp(0, scale.len() as i32 - 1) as usize;
            // On strong beats, snap to the nearest chord tone (any octave).
            if beat.fract() == 0.0 && bar_beat.is_multiple_of(2) {
                let chord = chord_at(bar_beat / 4);
                if let Some((nearest, _)) = scale
                    .iter()
                    .enumerate()
                    .filter(|(_, note)| chord.iter().any(|tone| (*note - tone).rem_euclid(12) == 0))
                    .min_by_key(|(i, _)| (*i as i32 - index as i32).abs())
                {
                    index = nearest;
                }
            }
            notes.push((beat, length.min(beats as f64 - beat), scale[index]));
        }
        beat += length;
    }
    notes
}

/// Blends the last `overlap` seconds of a longer render into its start, giving a loop of
/// `seconds` that repeats without a click (for continuous noise).
fn seamless(mut samples: Vec<f64>, seconds: f64, overlap: f64, rate: f64) -> Vec<f64> {
    let len = (seconds * rate).round() as usize;
    let blend = ((overlap * rate).round() as usize).min(samples.len().saturating_sub(len));
    for i in 0..blend {
        let w = i as f64 / blend as f64;
        samples[i] = samples[i] * w + samples[len + i] * (1.0 - w);
    }
    samples.truncate(len);
    samples
}

fn normalized(samples: &[f64], peak: f64) -> Vec<f32> {
    let max = samples.iter().fold(0.0_f64, |max, sample| max.max(sample.abs()));
    let scale = if max > 0.0 { peak / max } else { 0.0 };
    samples.iter().map(|sample| (sample * scale) as f32).collect()
}

// ---- music ----

fn day(rate: f64) -> Vec<f32> {
    const BPM: f64 = 92.0;
    let beat = 60.0 / BPM;
    // C, Am, F, G, C, Am, Dm, G, C, Em, F, C, Dm, G, C, G (root notes around C3).
    let roots = [48, 45, 41, 43, 48, 45, 50, 43, 48, 52, 41, 48, 50, 43, 48, 43];
    let minor =
        [false, true, false, false, false, true, true, false, false, true, false, false, true, false, false, false];
    let chord_at = |bar: usize| {
        let root = roots[bar % roots.len()];
        [root, root + if minor[bar % roots.len()] { 3 } else { 4 }, root + 7]
    };
    let bars = roots.len();
    let mut track = Track::new(bars as f64 * 4.0 * beat, rate);
    for bar in 0..bars {
        let start = bar as f64 * 4.0 * beat;
        let chord = chord_at(bar);
        for tone in chord {
            pad(&mut track, start, 4.0 * beat, tone + 12, 0.07);
        }
        // Bass on beats 1 and 3, the fifth on 3 every other bar.
        bass(&mut track, start, 1.8 * beat, chord[0] - 12, 0.3);
        let third = if bar % 2 == 1 { chord[2] - 12 } else { chord[0] - 12 };
        bass(&mut track, start + 2.0 * beat, 1.8 * beat, third, 0.26);
        // A light arpeggio on the off-beats.
        for (i, tone) in [chord[0], chord[1], chord[2], chord[1]].iter().enumerate() {
            lead(&mut track, start + (i as f64 + 0.5) * beat, 0.4 * beat, tone + 24, 0.05);
        }
    }
    // C major pentatonic, C4..A5.
    let scale = [60, 62, 64, 67, 69, 72, 74, 76, 79, 81];
    for (at, length, note) in melody(0x5eed_da11, bars * 4, &scale, chord_at) {
        lead(&mut track, at * beat, length * beat * 0.9, note, 0.16);
    }
    track.finish(0.75)
}

fn night(rate: f64) -> Vec<f32> {
    const BPM: f64 = 66.0;
    let beat = 60.0 / BPM;
    // Am, F, C, G, Am, F, Em, Am.
    let chords: [[i32; 3]; 8] = [
        [45, 48, 52],
        [41, 45, 48],
        [48, 52, 55],
        [43, 47, 50],
        [45, 48, 52],
        [41, 45, 48],
        [40, 43, 47],
        [45, 48, 52],
    ];
    let chord_at = |bar: usize| chords[bar % chords.len()];
    let bars = chords.len();
    let mut track = Track::new(bars as f64 * 4.0 * beat, rate);
    for (bar, chord) in chords.iter().enumerate() {
        let start = bar as f64 * 4.0 * beat;
        for tone in chord {
            pad(&mut track, start, 4.0 * beat, tone + 12, 0.06);
        }
        bass(&mut track, start, 3.6 * beat, chord[0] - 12, 0.22);
    }
    // A minor pentatonic, A4..A5, in bells.
    let scale = [69, 72, 74, 76, 79, 81];
    for (at, _, note) in melody(0x0071_6487, bars * 4, &scale, chord_at) {
        bell(&mut track, at * beat, note, 0.1);
    }
    track.finish(0.6)
}

// ---- ambience ----

/// Wind: brown noise, gently swelling.
fn wind(samples: &mut [f64], rate: f64, noise: &mut Noise, gain: f64) {
    let mut brown = 0.0;
    let mut smooth = 0.0;
    let cutoff = 1.0 - (-TAU * 400.0 / rate).exp();
    for (i, sample) in samples.iter_mut().enumerate() {
        brown = (brown + noise.white() * 0.02) * 0.998;
        smooth += cutoff * (brown - smooth);
        let t = i as f64 / rate;
        let swell = 0.6 + 0.4 * (TAU * t / 9.0).sin() * (TAU * t / 4.3).cos();
        *sample += gain * smooth * 8.0 * swell;
    }
}

fn birds(rate: f64) -> Vec<f32> {
    const SECONDS: f64 = 24.0;
    const OVERLAP: f64 = 1.0;
    let mut samples = vec![0.0; ((SECONDS + OVERLAP) * rate).round() as usize];
    let mut noise = Noise(0xb1_4d5);
    wind(&mut samples, rate, &mut noise, 0.05);
    let mut track = Track { rate, samples };
    // Chirps: a phrase of 2–5 quick up-sweeps every few seconds.
    let mut at = 0.7;
    while at < SECONDS {
        let notes = 2 + (noise.next() * 4.0) as usize;
        let base = 2400.0 + noise.next() * 1400.0;
        let gain = 0.05 + noise.next() * 0.05;
        for n in 0..notes {
            let start = at + n as f64 * (0.09 + noise.next() * 0.05);
            let length = 0.05 + noise.next() * 0.05;
            let sweep = 600.0 + noise.next() * 900.0;
            track.voice(start, length, gain, |t| {
                let f = base + sweep * (t / length);
                (TAU * f * t).sin() * (t / length * std::f64::consts::PI).sin()
            });
        }
        at += 1.2 + noise.next() * 3.0;
    }
    normalized(&seamless(track.samples, SECONDS, OVERLAP, rate), 0.5)
}

fn crickets(rate: f64) -> Vec<f32> {
    const SECONDS: f64 = 16.0;
    const OVERLAP: f64 = 1.0;
    let mut samples = vec![0.0; ((SECONDS + OVERLAP) * rate).round() as usize];
    let mut noise = Noise(0xc41c_4e75);
    wind(&mut samples, rate, &mut noise, 0.025);
    let mut track = Track { rate, samples };
    // Two crickets: trains of three short pulses, about once a second each.
    for (pitch, mut at, gain) in [(4600.0, 0.2, 0.08), (5200.0, 0.65, 0.05)] {
        while at < SECONDS {
            for pulse in 0..3 {
                track.voice(at + pulse as f64 * 0.045, 0.025, gain, |t| {
                    (TAU * pitch * t).sin() * (t / 0.025 * std::f64::consts::PI).sin()
                });
            }
            at += 0.8 + noise.next() * 0.5;
        }
    }
    normalized(&seamless(track.samples, SECONDS, OVERLAP, rate), 0.4)
}

fn rain(rate: f64) -> Vec<f32> {
    const SECONDS: f64 = 12.0;
    const OVERLAP: f64 = 1.0;
    let len = ((SECONDS + OVERLAP) * rate).round() as usize;
    let mut noise = Noise(0x4a1_0f00);
    // Band-limited noise: a low-pass for the hiss, minus a slower one to drop the rumble.
    let (fast, slow) = (1.0 - (-TAU * 3500.0 / rate).exp(), 1.0 - (-TAU * 300.0 / rate).exp());
    let (mut low, mut lower) = (0.0, 0.0);
    let mut samples = Vec::with_capacity(len);
    for _ in 0..len {
        let white = noise.white();
        low += fast * (white - low);
        lower += slow * (white - lower);
        samples.push(0.5 * (low - lower));
    }
    let mut track = Track { rate, samples };
    // Drops: short pings now and then.
    let mut at = 0.0;
    while at < SECONDS {
        let f = 1200.0 + noise.next() * 2400.0;
        let gain = 0.02 + noise.next() * 0.04;
        track.voice(at, 0.06, gain, |t| (TAU * f * t).sin() * (-t / 0.012).exp());
        at += 0.02 + noise.next() * 0.12;
    }
    normalized(&seamless(track.samples, SECONDS, OVERLAP, rate), 0.45)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_loop_renders_audible_bounded_and_repeatable() {
        for name in MUSIC_LOOPS.iter().chain(AMBIENCE_LOOPS) {
            let samples = render_loop(name, 8000).expect(name);
            assert!(samples.len() > 8000 * 8, "{name} is at least 8 s long");
            assert!(samples.iter().all(|s| s.is_finite() && s.abs() <= 1.0), "{name} stays in range");
            let loud = samples.iter().fold(0.0f32, |max, s| max.max(s.abs()));
            assert!(loud > 0.3, "{name} is audible ({loud})");
            assert_eq!(render_loop(name, 8000).unwrap(), samples, "{name} renders the same every time");
        }
        assert!(render_loop("no-such-loop", 8000).is_none());
    }

    #[test]
    fn loops_join_without_a_jump() {
        // The step from the last sample back to the first is no bigger than steps inside.
        for name in AMBIENCE_LOOPS {
            let samples = render_loop(name, 8000).unwrap();
            let largest = samples.windows(2).fold(0.0f32, |max, pair| max.max((pair[1] - pair[0]).abs()));
            let join = (samples[0] - samples[samples.len() - 1]).abs();
            assert!(join <= largest, "{name}: join {join} > largest step {largest}");
        }
    }

    #[test]
    fn cues_follow_time_weather_and_volume() {
        assert_eq!(MusicCue::new(Some(MUSIC_DAY), None, 0.0), MusicCue::default());
        assert_eq!(MusicCue::new(Some(MUSIC_DAY), None, f32::NAN), MusicCue::default());
        let cue = MusicCue::new(Some(MUSIC_DAY), Some(AMBIENCE_BIRDS), 0.5);
        assert_eq!((cue.music_gain, cue.ambience_gain), (0.5, 0.4));
    }
}
