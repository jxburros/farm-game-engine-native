use std::process::Command;

#[test]
fn executable_loads_the_compiled_cartridge() {
    let cart = [env!("CARGO_MANIFEST_DIR"), "..", "..", "fixtures", "golden", "cartridges", "project-v8.cart"]
        .iter()
        .collect::<std::path::PathBuf>();
    let output = Command::new(env!("CARGO_BIN_EXE_farm-player"))
        .args(["--headless", "--cart", cart.to_str().unwrap()])
        .output()
        .unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    let report: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(report["gameId"], "local.project-1");
    assert!(report["stateHash"].as_str().is_some_and(|hash| hash.len() == 16));

    let replay = cart.with_file_name("twenty-ticks.json");
    let replayed = Command::new(env!("CARGO_BIN_EXE_farm-player"))
        .args(["--headless", "--cart", cart.to_str().unwrap(), "--replay", replay.to_str().unwrap()])
        .output()
        .unwrap();
    assert!(replayed.status.success(), "{}", String::from_utf8_lossy(&replayed.stderr));
    let replay_report: serde_json::Value = serde_json::from_slice(&replayed.stdout).unwrap();
    assert_ne!(report["stateHash"], replay_report["stateHash"]);
}
