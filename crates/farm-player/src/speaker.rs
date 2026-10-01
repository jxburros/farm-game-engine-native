//! The audio device (cpal, feature `audio-out`): a stream whose callback mixes the sounds the
//! game sends. Without a device at start-up (headless machines, CI) the game simply stays silent.
//!
//! - The callback never allocates: every preset is rendered when the stream opens
//!   ([`SoundBank`]), sounds travel as shared sample buffers through a bounded channel, and the
//!   bank keeps every buffer alive, so the callback never frees one either.
//! - Device loss (headphones unplugged, a Bluetooth device gone) or a new default device reopens
//!   the stream on the current default device, retrying with a growing delay;
//!   [`Audio::maintain`] does that and is called every frame.
//! - Any sample format cpal offers is converted from the mixer's `f32`.
//!
//! [`Audio`] belongs to the thread that opened it (cpal streams cannot move between threads);
//! [`SpeakerThread`] keeps one on a thread of its own for hosts that drive the player from
//! different threads (the editor's Play Mode).
// Wall-clock time paces device checks and retries here; it never reaches the simulation.
#![allow(clippy::disallowed_types, clippy::disallowed_methods)]

use crate::audio::{Mixer, SoundBank, SoundRequest, Voice};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{DeviceId, ErrorKind, FromSample, SampleFormat, SizedSample, Stream, StreamConfig};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender, SyncSender};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Sounds waiting for the callback; more in one frame are dropped.
const QUEUE: usize = 64;
/// How often the default output device is checked for a change.
const DEVICE_CHECK: Duration = Duration::from_secs(2);
/// First and longest delay before reopening a lost stream.
const RETRY_FIRST: Duration = Duration::from_millis(500);
const RETRY_MAX: Duration = Duration::from_secs(8);
/// Mixer samples prepared up front (larger device buffers grow it once).
const SCRATCH_SAMPLES: usize = 16 * 1024;

/// Whether a stream error means the stream is gone (and must be reopened).
pub fn is_fatal(kind: ErrorKind) -> bool {
    !matches!(kind, ErrorKind::Xrun | ErrorKind::DeviceChanged | ErrorKind::RealtimeDenied)
}

/// An open stream and what feeds it.
struct Output {
    _stream: Stream,
    sender: SyncSender<Voice>,
    bank: SoundBank,
    /// The device the stream plays on (to notice a new default device).
    device: Option<DeviceId>,
    /// Set by the error callback when the stream died.
    failed: Arc<AtomicBool>,
}

/// A running output stream that follows the default device.
pub struct Audio {
    output: Option<Output>,
    /// When to check the default device or retry opening it next.
    next_check: Instant,
    retry_delay: Duration,
}

impl std::fmt::Debug for Audio {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Audio").field("open", &self.output.is_some()).finish_non_exhaustive()
    }
}

/// Builds a stream of samples of type `T` mixing what arrives on `receiver`.
fn build<T>(
    device: &cpal::Device,
    config: StreamConfig,
    receiver: Receiver<Voice>,
    failed: Arc<AtomicBool>,
) -> Result<Stream, cpal::Error>
where
    T: SizedSample + FromSample<f32> + Send + 'static,
{
    let channels = usize::from(config.channels);
    let mut mixer = Mixer::new(config.sample_rate);
    let mut scratch = vec![0.0f32; SCRATCH_SAMPLES];
    device.build_output_stream::<T, _, _>(
        config,
        move |data: &mut [T], _: &cpal::OutputCallbackInfo| {
            while let Ok(voice) = receiver.try_recv() {
                mixer.start(voice);
            }
            if scratch.len() < data.len() {
                // Only when the device asks for a larger buffer than ever before.
                scratch.resize(data.len(), 0.0);
            }
            let mix = &mut scratch[..data.len()];
            mixer.mix(mix, channels);
            for (out, sample) in data.iter_mut().zip(mix.iter()) {
                *out = T::from_sample(*sample);
            }
        },
        move |error: cpal::Error| {
            eprintln!("Audio stream error: {error}");
            if is_fatal(error.kind()) {
                failed.store(true, Ordering::Relaxed);
            }
        },
        None,
    )
}

/// Opens the default output device; `None` when there is none, it refuses, or it wants a sample
/// format cpal cannot convert to.
fn open() -> Option<Output> {
    let host = cpal::default_host();
    let device = host.default_output_device()?;
    let supported = device.default_output_config().ok()?;
    let format = supported.sample_format();
    let config = supported.config();
    let bank = SoundBank::new(config.sample_rate);
    let (sender, receiver) = mpsc::sync_channel::<Voice>(QUEUE);
    let failed = Arc::new(AtomicBool::new(false));
    let flag = Arc::clone(&failed);
    let stream = match format {
        SampleFormat::F32 => build::<f32>(&device, config, receiver, flag),
        SampleFormat::F64 => build::<f64>(&device, config, receiver, flag),
        SampleFormat::I8 => build::<i8>(&device, config, receiver, flag),
        SampleFormat::I16 => build::<i16>(&device, config, receiver, flag),
        SampleFormat::I24 => build::<cpal::I24>(&device, config, receiver, flag),
        SampleFormat::I32 => build::<i32>(&device, config, receiver, flag),
        SampleFormat::I64 => build::<i64>(&device, config, receiver, flag),
        SampleFormat::U8 => build::<u8>(&device, config, receiver, flag),
        SampleFormat::U16 => build::<u16>(&device, config, receiver, flag),
        SampleFormat::U24 => build::<cpal::U24>(&device, config, receiver, flag),
        SampleFormat::U32 => build::<u32>(&device, config, receiver, flag),
        SampleFormat::U64 => build::<u64>(&device, config, receiver, flag),
        other => {
            eprintln!("Audio unavailable: the output device wants {other} samples.");
            return None;
        }
    };
    let stream = match stream {
        Ok(stream) => stream,
        Err(error) => {
            eprintln!("Audio unavailable: {error}");
            return None;
        }
    };
    stream.play().ok()?;
    Some(Output { _stream: stream, sender, bank, device: device.id().ok(), failed })
}

/// The current default output device.
fn default_device() -> Option<DeviceId> {
    cpal::default_host().default_output_device().and_then(|device| device.id().ok())
}

impl Audio {
    /// Opens the default output device; `None` when there is none or it refuses.
    pub fn start() -> Option<Self> {
        let output = open()?;
        Some(Self { output: Some(output), next_check: Instant::now() + DEVICE_CHECK, retry_delay: RETRY_FIRST })
    }

    /// Plays a sound (silently dropped while the device is gone).
    pub fn play(&self, request: SoundRequest) {
        if let Some(output) = &self.output {
            if let Some(voice) = output.bank.voice(&request) {
                let _ = output.sender.try_send(voice);
            }
        }
    }

    /// Whether a stream is open now.
    pub fn is_open(&self) -> bool {
        self.output.as_ref().is_some_and(|output| !output.failed.load(Ordering::Relaxed))
    }

    /// Reopens the stream after a device loss, or on the new default device after a change.
    /// Cheap between checks; call it every frame.
    pub fn maintain(&mut self) {
        let failed = self.output.as_ref().is_none_or(|output| output.failed.load(Ordering::Relaxed));
        if failed && self.output.is_some() {
            eprintln!("Audio device lost; trying to reopen it.");
            self.output = None;
            self.retry_delay = RETRY_FIRST;
            self.next_check = Instant::now() + self.retry_delay;
            return;
        }
        let now = Instant::now();
        if now < self.next_check {
            return;
        }
        match &self.output {
            None => match open() {
                Some(output) => {
                    self.output = Some(output);
                    self.retry_delay = RETRY_FIRST;
                    self.next_check = now + DEVICE_CHECK;
                }
                None => {
                    self.retry_delay = (self.retry_delay * 2).min(RETRY_MAX);
                    self.next_check = now + self.retry_delay;
                }
            },
            Some(output) => {
                // A new default device (headphones plugged in, the system's choice changed).
                let current = default_device();
                if current.is_some() && output.device.is_some() && current != output.device {
                    self.output = None;
                    self.output = open();
                }
                self.next_check = now + DEVICE_CHECK;
            }
        }
    }
}

/// An output stream on its own thread: `Send`, so its owner may move between threads. Silent
/// when there is no device. Dropping it closes the stream.
#[derive(Debug)]
pub struct SpeakerThread {
    sender: Option<Sender<SoundRequest>>,
    thread: Option<JoinHandle<()>>,
}

impl SpeakerThread {
    pub fn start() -> Self {
        let (sender, receiver) = mpsc::channel::<SoundRequest>();
        let thread = std::thread::Builder::new()
            .name("farm-audio".to_owned())
            .spawn(move || {
                let mut audio = Audio::start();
                loop {
                    match receiver.recv_timeout(Duration::from_millis(250)) {
                        Ok(request) => {
                            if let Some(audio) = &audio {
                                audio.play(request);
                            }
                        }
                        Err(mpsc::RecvTimeoutError::Timeout) => {}
                        Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    }
                    if let Some(audio) = audio.as_mut() {
                        audio.maintain();
                    }
                }
            })
            .ok();
        Self { sender: Some(sender), thread }
    }

    pub fn play(&self, request: SoundRequest) {
        if let Some(sender) = &self.sender {
            let _ = sender.send(request);
        }
    }
}

impl Drop for SpeakerThread {
    fn drop(&mut self) {
        // Closing the channel ends the thread's loop, which drops the stream.
        self.sender.take();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_lost_streams_are_reopened() {
        assert!(is_fatal(ErrorKind::DeviceNotAvailable));
        assert!(is_fatal(ErrorKind::StreamInvalidated));
        assert!(is_fatal(ErrorKind::HostUnavailable));
        assert!(!is_fatal(ErrorKind::Xrun));
        assert!(!is_fatal(ErrorKind::DeviceChanged), "the stream followed the new default device itself");
    }
}
