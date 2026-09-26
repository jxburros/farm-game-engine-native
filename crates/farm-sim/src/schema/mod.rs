//! The data shapes (port of `src/FarmEngine.Schemas`, itself a port of
//! `packages/engine-schemas`). One C# file → one module here, everything re-exported flat.
//!
//! Serialization rules (docs/PORTING.md, carried over): every number is an `f64`, JSON names are
//! camelCase, optional fields are omitted when `None`, present-as-null fields (zod `.nullable()`)
//! are plain `Option`s that serialize `null`, property initializers are the struct's `Default`
//! (applied when a key is missing), and zod `.passthrough()` records carry unknown keys in a
//! flattened `extra` map.

pub mod actors;
pub mod animals;
pub mod content;
pub mod crafting;
pub mod economy;
pub mod events;
pub mod extensibility;
pub mod fishing;
pub mod game_content;
pub mod graphics;
pub mod interface;
pub mod mining;
pub mod nodes;
pub mod packs;
pub mod primitives;
pub mod project;
pub mod quests;
pub mod save;
pub mod settings;
pub mod social;
pub mod weather;
pub mod world;

pub use actors::*;
pub use animals::*;
pub use content::*;
pub use crafting::*;
pub use economy::*;
pub use events::*;
pub use extensibility::*;
pub use fishing::*;
pub use game_content::*;
pub use graphics::*;
pub use interface::*;
pub use mining::*;
pub use nodes::*;
pub use packs::*;
pub use primitives::*;
pub use project::*;
pub use quests::*;
pub use save::*;
pub use settings::*;
pub use social::*;
pub use weather::*;
pub use world::*;
