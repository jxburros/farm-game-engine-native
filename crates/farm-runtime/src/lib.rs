//! `farm-runtime`: fixed timestep, input bindings, minigame kinds, panel model and audio cues
//! (port of `FarmEngine.Runtime`). Hosts (the editor's Play Mode, `farm-player`) feed raw
//! key and pad events in and get commands out; nothing here touches the OS.
#![forbid(unsafe_code)]

pub mod timestep;
