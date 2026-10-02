//! Writes each fuzz target's seed corpus (`Target::seeds`: fixtures and hand-written inputs) to
//! `fuzz/corpus/<target>/` for cargo-fuzz: `cargo run -p farm-fuzz --example write_corpus`.

use farm_fuzz::Target;
use std::path::PathBuf;

fn main() -> std::io::Result<()> {
    let corpus: PathBuf = [env!("CARGO_MANIFEST_DIR"), "..", "..", "fuzz", "corpus"].iter().collect();
    for target in Target::ALL {
        let dir = corpus.join(target.name());
        std::fs::create_dir_all(&dir)?;
        let seeds = target.seeds();
        for (index, seed) in seeds.iter().enumerate() {
            std::fs::write(dir.join(format!("seed-{index:02}")), seed)?;
        }
        println!("{}: {} seeds in {}", target.name(), seeds.len(), dir.display());
    }
    Ok(())
}
