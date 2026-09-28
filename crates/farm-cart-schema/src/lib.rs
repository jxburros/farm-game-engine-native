//! Machine-generated FlatBuffers accessors. Isolated because flatc emits verified unsafe
//! pointer traversal; all hand-written simulation and cartridge code keeps unsafe forbidden.
#![allow(unsafe_code, clippy::all, dead_code, unused_imports, mismatched_lifetime_syntaxes)]

/// The FlatBuffers runtime the generated code was written for (builders for writers).
pub use flatbuffers;

mod cart_generated {
    include!("cart_generated.rs");
}

mod save_generated {
    include!("save_generated.rs");
}

/// `schemas/cart.fbs` (namespace `FarmEngine.Cart`) and `schemas/save.fbs` (`FarmEngine.Save`).
pub mod farm_engine {
    pub mod cart {
        pub use crate::cart_generated::farm_engine::cart::*;
    }

    pub mod save {
        pub use crate::save_generated::farm_engine::save::*;
    }
}
