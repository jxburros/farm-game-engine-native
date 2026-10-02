//! `farm-runtime`: fixed timestep, input bindings, minigame kinds, panel model and audio cues
//! (port of the retired C# `FarmEngine.Runtime`). Hosts (the editor's Play Mode, `farm-player`) feed raw
//! key and pad events in and get commands out; nothing here touches the OS.
//!
//! One module per C# file: [`timestep`] (`FixedTimestep.cs`), [`input`] (`Input.cs`),
//! [`minigames`] (`Minigames.cs`), [`panels`] (`GamePanels.cs`) and [`audio`] (the model half
//! of `Audio.cs`). The Jint plugin sandbox (`Plugins.cs`) becomes `farm-plugins`. [`music`] (the
//! built-in music and ambience loops) is new in the native engine.
#![forbid(unsafe_code)]
#![deny(clippy::disallowed_types, clippy::disallowed_methods)]

pub mod audio;
pub mod host;
pub mod input;
pub mod minigames;
pub mod music;
pub mod panels;
pub mod timestep;
