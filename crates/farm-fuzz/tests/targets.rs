//! The fuzz targets on stable (#116): each over its seed corpus, over arbitrary bytes, and over
//! seeds with random edits (flipped, inserted, deleted and duplicated bytes, truncation), which
//! reach past the parsers far more often than random bytes do.
//!
//! A failure prints the input (proptest shrinks it first). To replay a run, set
//! `PROPTEST_RNG_SEED` to the seed it printed; for longer runs raise `PROPTEST_CASES`. The
//! cargo-fuzz targets in `fuzz/` run the same entry points under libFuzzer.

use farm_fuzz::Target;
use proptest::prelude::*;

#[test]
fn every_target_runs_clean_on_its_seed_corpus() {
    for target in Target::ALL {
        let seeds = target.seeds();
        assert!(!seeds.is_empty(), "{} has no seeds", target.name());
        for seed in seeds {
            target.run(&seed);
        }
    }
}

#[test]
fn every_target_runs_clean_on_empty_and_tiny_inputs() {
    for target in Target::ALL {
        for data in [&b""[..], b"\0", b"\xff", b"{}", b"[]", b"null", b"[null]", b"\"\"", b"FGCT", b"FGSV"] {
            target.run(data);
        }
    }
}

/// One edit to a seed.
#[derive(Debug, Clone)]
enum Edit {
    Flip { at: usize, bit: u8 },
    Set { at: usize, byte: u8 },
    Insert { at: usize, bytes: Vec<u8> },
    Delete { at: usize, len: usize },
    Duplicate { at: usize, len: usize },
    Truncate { at: usize },
}

fn edit() -> impl Strategy<Value = Edit> {
    let at = any::<usize>;
    prop_oneof![
        3 => (at(), 0u8..8).prop_map(|(at, bit)| Edit::Flip { at, bit }),
        3 => (at(), prop_oneof![any::<u8>(), Just(b'0'), Just(b'9'), Just(b'-'), Just(b'"'), Just(b'e'), Just(0xff)])
            .prop_map(|(at, byte)| Edit::Set { at, byte }),
        2 => (at(), proptest::collection::vec(any::<u8>(), 1..8)).prop_map(|(at, bytes)| Edit::Insert { at, bytes }),
        2 => (at(), 1usize..64).prop_map(|(at, len)| Edit::Delete { at, len }),
        1 => (at(), 1usize..64).prop_map(|(at, len)| Edit::Duplicate { at, len }),
        1 => at().prop_map(|at| Edit::Truncate { at }),
    ]
}

fn apply(seed: &[u8], edits: &[Edit]) -> Vec<u8> {
    let mut data = seed.to_vec();
    for edit in edits {
        let len = data.len();
        let at = |at: usize| if len == 0 { 0 } else { at % len };
        match edit {
            Edit::Flip { at: i, bit } if len > 0 => data[at(*i)] ^= 1 << bit,
            Edit::Set { at: i, byte } if len > 0 => data[at(*i)] = *byte,
            Edit::Insert { at: i, bytes } => {
                let i = at(*i);
                data.splice(i..i, bytes.iter().copied());
            }
            Edit::Delete { at: i, len: n } if len > 0 => {
                let i = at(*i);
                data.drain(i..(i + n).min(len));
            }
            Edit::Duplicate { at: i, len: n } if len > 0 => {
                let i = at(*i);
                let chunk = data[i..(i + n).min(len)].to_vec();
                data.splice(i..i, chunk);
            }
            Edit::Truncate { at: i } => data.truncate(at(*i)),
            _ => {}
        }
    }
    data
}

/// Cases per target: the cheap parsers get more.
fn cases(target: Target) -> u32 {
    match target {
        Target::SaveMigration | Target::Image | Target::PluginMutations | Target::RenderRequest => 96,
        Target::Commands | Target::Save | Target::Session | Target::Preview => 48,
        Target::Cartridge | Target::Project => 24,
    }
}

fn check_arbitrary(target: Target) {
    let config = ProptestConfig { cases: cases(target), ..ProptestConfig::default() };
    proptest!(config, |(data in proptest::collection::vec(any::<u8>(), 0..512))| {
        target.run(&data);
    });
}

fn check_edited_seeds(target: Target) {
    let seeds = target.seeds();
    let config = ProptestConfig { cases: cases(target), ..ProptestConfig::default() };
    proptest!(config, |(index in any::<usize>(), edits in proptest::collection::vec(edit(), 1..6))| {
        let data = apply(&seeds[index % seeds.len()], &edits);
        target.run(&data);
    });
}

macro_rules! target_tests {
    ($($name:ident => $target:expr,)*) => {
        mod arbitrary_bytes {
            use super::*;
            $(#[test] fn $name() { check_arbitrary($target); })*
        }
        mod edited_seeds {
            use super::*;
            $(#[test] fn $name() { check_edited_seeds($target); })*
        }
    };
}

target_tests! {
    cartridge => Target::Cartridge,
    save => Target::Save,
    save_migration => Target::SaveMigration,
    image => Target::Image,
    project => Target::Project,
    commands => Target::Commands,
    render_request => Target::RenderRequest,
    preview => Target::Preview,
    session => Target::Session,
    plugin_mutations => Target::PluginMutations,
}
