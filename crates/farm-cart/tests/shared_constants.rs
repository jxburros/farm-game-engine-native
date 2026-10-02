//! Constants that the Rust engine and the F# authoring core both define must agree: a pack the
//! editor accepts must load in the player, and a cartridge the editor compiles must have the
//! format the player reads. (Saves are written by Rust only; F# has no save version.)

use std::path::PathBuf;

fn authoring_source(file: &str) -> String {
    let path: PathBuf = [env!("CARGO_MANIFEST_DIR"), "..", "..", "src", "FarmEngine.Authoring", file].iter().collect();
    std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()))
}

/// The text after `marker` on the first line that contains it, up to the end of the line.
fn after<'a>(source: &'a str, marker: &str, file: &str) -> &'a str {
    let line = source.lines().find(|line| line.contains(marker)).unwrap_or_else(|| panic!("{file} has no `{marker}`"));
    line[line.find(marker).expect("marker") + marker.len()..].trim()
}

#[test]
fn engine_version_matches_the_authoring_core() {
    let source = authoring_source("PackRules.fs");
    let fsharp = after(&source, "let EngineVersion =", "PackRules.fs").trim_matches('"');
    assert_eq!(
        fsharp,
        farm_sim::schema::packs::ENGINE_VERSION,
        "PackRules.EngineVersion (F#) and farm_sim::schema::packs::ENGINE_VERSION differ"
    );
}

#[test]
fn cart_format_matches_the_cartridge_compiler() {
    let source = authoring_source("CartridgeCompiler.fs");
    let fsharp = after(&source, "static member Format =", "CartridgeCompiler.fs").trim_end_matches('u');
    assert_eq!(
        fsharp.parse::<u32>().expect("CartridgeCompiler.Format is a uint32 literal"),
        farm_cart::CART_FORMAT,
        "CartridgeCompiler.Format (F#) and farm_cart::CART_FORMAT differ"
    );
}

#[test]
fn the_authoring_core_declares_no_save_version() {
    // F# used to carry a stale copy (4.0 while Rust was at 5); saves are Rust's alone.
    let source = authoring_source("SchemaConstants.fs");
    assert!(!source.contains("CurrentSaveVersion"), "SchemaConstants.fs declares a save version again");
}
