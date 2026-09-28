//! The audio device (cpal): a stream whose callback drains sound requests into a [`Mixer`].
//! Without a device (headless machines, CI) the game simply stays silent.

use crate::audio::{Mixer, SoundRequest};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{SampleFormat, Stream};
use std::sync::mpsc::{self, Receiver, Sender};

/// A running output stream.
pub struct Audio {
    _stream: Stream,
    sender: Sender<SoundRequest>,
}

impl std::fmt::Debug for Audio {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Audio").finish_non_exhaustive()
    }
}

fn drain(receiver: &Receiver<SoundRequest>, mixer: &mut Mixer) {
    while let Ok(request) = receiver.try_recv() {
        mixer.play(&request);
    }
}

impl Audio {
    /// Opens the default output device; `None` when there is none or it refuses.
    pub fn start() -> Option<Self> {
        let host = cpal::default_host();
        let device = host.default_output_device()?;
        let supported = device.default_output_config().ok()?;
        let format = supported.sample_format();
        let config = supported.config();
        let channels = usize::from(config.channels);
        let mut mixer = Mixer::new(config.sample_rate);
        let (sender, receiver) = mpsc::channel::<SoundRequest>();
        let on_error = |error| eprintln!("Audio stream error: {error}");
        let stream = match format {
            SampleFormat::F32 => device
                .build_output_stream(
                    config,
                    move |data: &mut [f32], _: &cpal::OutputCallbackInfo| {
                        drain(&receiver, &mut mixer);
                        mixer.mix(data, channels);
                    },
                    on_error,
                    None,
                )
                .ok()?,
            SampleFormat::I16 => {
                let mut scratch = Vec::new();
                device
                    .build_output_stream(
                        config,
                        move |data: &mut [i16], _: &cpal::OutputCallbackInfo| {
                            drain(&receiver, &mut mixer);
                            scratch.resize(data.len(), 0.0f32);
                            mixer.mix(&mut scratch, channels);
                            for (out, sample) in data.iter_mut().zip(&scratch) {
                                *out = (sample * f32::from(i16::MAX)) as i16;
                            }
                        },
                        on_error,
                        None,
                    )
                    .ok()?
            }
            SampleFormat::U16 => {
                let mut scratch = Vec::new();
                device
                    .build_output_stream(
                        config,
                        move |data: &mut [u16], _: &cpal::OutputCallbackInfo| {
                            drain(&receiver, &mut mixer);
                            scratch.resize(data.len(), 0.0f32);
                            mixer.mix(&mut scratch, channels);
                            for (out, sample) in data.iter_mut().zip(&scratch) {
                                *out = ((sample * 0.5 + 0.5) * f32::from(u16::MAX)) as u16;
                            }
                        },
                        on_error,
                        None,
                    )
                    .ok()?
            }
            _ => return None,
        };
        stream.play().ok()?;
        Some(Self { _stream: stream, sender })
    }

    pub fn play(&self, request: SoundRequest) {
        let _ = self.sender.send(request);
    }
}
