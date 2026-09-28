//! Shared, read-only rendering preparation for editor previews and game players.
//! Hosts execute snapshots on their graphics backend; this crate never mutates simulation state.
#![forbid(unsafe_code)]

pub mod graphics;
pub mod shell;
pub mod snapshot;
