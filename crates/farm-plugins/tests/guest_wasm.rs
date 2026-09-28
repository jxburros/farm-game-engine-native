//! The checked-in guest must be the build its README records.

use sha2::{Digest, Sha256};
use std::path::PathBuf;

fn guest_dir() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "guest"].iter().collect()
}

/// The value of the README's `- {key}: ` line, without backticks.
fn recorded(key: &str) -> String {
    let readme = std::fs::read_to_string(guest_dir().join("README.md")).expect("guest/README.md");
    let prefix = format!("- {key}: ");
    let line = readme.lines().find(|line| line.starts_with(&prefix)).unwrap_or_else(|| panic!("README records {key}"));
    line[prefix.len()..].trim().trim_matches('`').to_owned()
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

#[test]
fn the_checked_in_guest_matches_its_recorded_hash() {
    let on_disk = std::fs::read(guest_dir().join("farm_plugin_guest.wasm")).expect("guest wasm");
    assert_eq!(on_disk, farm_plugins::GUEST_WASM, "the embedded guest is the checked-in file");
    let hash = hex(&Sha256::digest(&on_disk));
    assert_eq!(
        hash,
        recorded("sha256"),
        "crates/farm-plugins/guest/farm_plugin_guest.wasm changed: rebuild it with tools/plugin-guest/build.sh \
         and record the new sha256 in crates/farm-plugins/guest/README.md"
    );
    assert_eq!(format!("{} bytes", on_disk.len()), recorded("size"));
}
