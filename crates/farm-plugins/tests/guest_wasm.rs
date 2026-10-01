//! The checked-in guest must be the build its README records, from the inputs it records, with
//! the memory layout and the metering the sandbox relies on.
//!
//! The hashes catch a guest that drifted from its README, or sources that changed without a
//! rebuild. They cannot catch a wrongly built binary whose README was edited to match: CI
//! rebuilds the guest from source and compares the bytes (`tools/plugin-guest/build.sh --check`).

use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use wasmparser::{BlockType, DataKind, ExternalKind, Operator, Parser, Payload, TypeRef, ValType};

fn guest_dir() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "guest"].iter().collect()
}

fn repository_root() -> PathBuf {
    [env!("CARGO_MANIFEST_DIR"), "..", ".."].iter().collect()
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

/// Every file under `dir` (relative to the repository root, `/`-separated), without `target/`.
fn files_under(root: &Path, dir: &str, out: &mut Vec<String>) {
    let entries = std::fs::read_dir(root.join(dir)).unwrap_or_else(|e| panic!("list {dir}: {e}"));
    for entry in entries {
        let entry = entry.expect("directory entry");
        let name = entry.file_name().to_string_lossy().into_owned();
        let relative = format!("{dir}/{name}");
        if entry.file_type().expect("file type").is_dir() {
            if name != "target" {
                files_under(root, &relative, out);
            }
        } else {
            out.push(relative);
        }
    }
}

/// The hash `tools/plugin-guest/build.sh` prints as "inputs sha256": for the guest crate, the
/// meter and the build script, sorted by path, one `path\tsha256(contents without CR)\n` line
/// each; then the sha256 of those lines. Carriage returns are dropped so a Windows checkout
/// hashes the same.
fn inputs_sha256() -> String {
    let root = repository_root();
    let mut files = vec!["tools/plugin-guest/build.sh".to_owned()];
    files_under(&root, "crates/farm-plugin-guest", &mut files);
    files_under(&root, "tools/plugin-guest/meter", &mut files);
    files.sort();
    let mut lines = String::new();
    for file in &files {
        let mut bytes = std::fs::read(root.join(file)).unwrap_or_else(|e| panic!("read {file}: {e}"));
        bytes.retain(|byte| *byte != b'\r');
        lines.push_str(&format!("{file}\t{}\n", hex(&Sha256::digest(&bytes))));
    }
    hex(&Sha256::digest(lines.as_bytes()))
}

#[test]
fn the_guest_sources_are_the_recorded_inputs() {
    assert_eq!(
        inputs_sha256(),
        recorded("inputs sha256"),
        "crates/farm-plugin-guest, tools/plugin-guest/meter or tools/plugin-guest/build.sh changed: rebuild the \
         guest with tools/plugin-guest/build.sh and record its sha256 and inputs sha256 in \
         crates/farm-plugins/guest/README.md"
    );
}

/// What the sandbox relies on, read from the binary.
#[derive(Debug, Default)]
struct Facts {
    /// Initial values of the defined mutable `i32` globals, in order (the first is
    /// `__stack_pointer`; names are stripped).
    mutable_i32_globals: Vec<i64>,
    /// Offsets of the active data segments.
    data_offsets: Vec<i64>,
    /// The `fp_fuel` export, as a global index.
    fuel: Option<u32>,
    imported_globals: u32,
    imported_functions: u32,
    /// Every defined function body, as its operators.
    bodies: Vec<Vec<Operator<'static>>>,
}

fn const_i32(expr: &wasmparser::ConstExpr<'_>) -> Option<i64> {
    match expr.get_operators_reader().read().ok()? {
        Operator::I32Const { value } => Some(i64::from(value)),
        _ => None,
    }
}

fn facts(wasm: &'static [u8]) -> Facts {
    let mut facts = Facts::default();
    for payload in Parser::new(0).parse_all(wasm) {
        match payload.expect("the guest parses") {
            Payload::ImportSection(section) => {
                for import in section {
                    match import.expect("import").ty {
                        TypeRef::Global(_) => facts.imported_globals += 1,
                        TypeRef::Func(_) => facts.imported_functions += 1,
                        _ => {}
                    }
                }
            }
            Payload::GlobalSection(section) => {
                for global in section {
                    let global = global.expect("global");
                    if global.ty.mutable && global.ty.content_type == ValType::I32 {
                        facts.mutable_i32_globals.push(const_i32(&global.init_expr).unwrap_or(-1));
                    }
                }
            }
            Payload::ExportSection(section) => {
                for export in section {
                    let export = export.expect("export");
                    if export.name == "fp_fuel" && export.kind == ExternalKind::Global {
                        facts.fuel = Some(export.index);
                    }
                }
            }
            Payload::DataSection(section) => {
                for data in section {
                    if let DataKind::Active { offset_expr, .. } = data.expect("data segment").kind {
                        facts.data_offsets.push(const_i32(&offset_expr).unwrap_or(-1));
                    }
                }
            }
            Payload::CodeSectionEntry(body) => {
                let mut reader = body.get_operators_reader().expect("operators");
                let mut ops = Vec::new();
                while !reader.eof() {
                    ops.push(reader.read().expect("operator"));
                }
                facts.bodies.push(ops);
            }
            _ => {}
        }
    }
    facts
}

const STACK_SIZE: i64 = 1 << 20;

#[test]
fn the_stack_comes_first_so_an_overflow_traps() {
    // crates/farm-plugin-guest/.cargo/config.toml: --stack-first -zstack-size=1048576. The
    // shadow stack grows down from 1 MiB towards address 0, so an overflow traps instead of
    // overwriting the heap or the data.
    let facts = facts(farm_plugins::GUEST_WASM);
    assert_eq!(facts.imported_globals, 0, "global indices start at the guest's own globals");
    assert_eq!(
        facts.mutable_i32_globals.first(),
        Some(&STACK_SIZE),
        "__stack_pointer (the first mutable i32 global) starts at 1 MiB"
    );
    assert!(!facts.data_offsets.is_empty(), "the guest has data segments");
    for offset in &facts.data_offsets {
        assert!(*offset >= STACK_SIZE, "a data segment starts at {offset}, inside the stack");
    }
}

/// `global.get fuel; i64.const 0; i64.lt_s; if; unreachable; end` at `ops[at..]`.
fn is_check(ops: &[Operator<'_>], at: usize, fuel: u32) -> bool {
    matches!(
        ops.get(at..at + 6),
        Some([
            Operator::GlobalGet { global_index },
            Operator::I64Const { value: 0 },
            Operator::I64LtS,
            Operator::If { blockty: BlockType::Empty },
            Operator::Unreachable,
            Operator::End,
        ]) if *global_index == fuel
    )
}

/// `global.get fuel; i64.const cost; i64.sub; global.set fuel` at `ops[at..]`.
fn is_charge(ops: &[Operator<'_>], at: usize, fuel: u32) -> bool {
    matches!(
        ops.get(at..at + 4),
        Some([
            Operator::GlobalGet { global_index: get },
            Operator::I64Const { value },
            Operator::I64Sub,
            Operator::GlobalSet { global_index: set },
        ]) if *get == fuel && *set == fuel && *value > 0
    )
}

/// A check right at `at`, or right after a charge there.
fn checked_at(ops: &[Operator<'_>], at: usize, fuel: u32) -> bool {
    is_check(ops, at, fuel) || (is_charge(ops, at, fuel) && is_check(ops, at + 4, fuel))
}

/// Whether `op` calls another function. The meter's own helper (the last function: it charges
/// bulk memory work and checks fuel itself) does not count, as in the meter.
fn calls(op: &Operator<'_>, helper: u32) -> bool {
    match op {
        Operator::Call { function_index } => *function_index != helper,
        Operator::CallIndirect { .. }
        | Operator::ReturnCall { .. }
        | Operator::ReturnCallIndirect { .. }
        | Operator::CallRef { .. }
        | Operator::ReturnCallRef { .. } => true,
        _ => false,
    }
}

#[test]
fn every_loop_and_every_calling_function_is_metered() {
    // tools/plugin-guest/meter: each loop iteration and each call passes a fuel check, so a
    // plugin that runs out of fuel stops (the host's budgets bound time).
    let facts = facts(farm_plugins::GUEST_WASM);
    let fuel = facts.fuel.expect("the guest exports fp_fuel");
    let defined = u32::try_from(facts.bodies.len()).expect("function count");
    let helper = facts.imported_functions + defined - 1;
    let mut loops = 0;
    let mut failures = Vec::new();
    for (function, ops) in facts.bodies.iter().enumerate() {
        if ops.iter().any(|op| calls(op, helper)) && !checked_at(ops, 0, fuel) {
            failures.push(format!("function {function} calls others but does not check fuel on entry"));
        }
        for (index, op) in ops.iter().enumerate() {
            if matches!(op, Operator::Loop { .. }) {
                loops += 1;
                if !checked_at(ops, index + 1, fuel) {
                    failures.push(format!("function {function}: the loop at operator {index} does not check fuel"));
                }
            }
            let bulk =
                matches!(op, Operator::MemoryCopy { .. } | Operator::MemoryFill { .. } | Operator::MemoryInit { .. });
            let charged = matches!(
                index.checked_sub(1).map(|before| &ops[before]),
                Some(Operator::Call { function_index }) if *function_index == helper
            );
            if bulk && !charged {
                failures.push(format!("function {function}: bulk memory at operator {index} is not charged"));
            }
        }
    }
    assert!(loops > 100, "the guest has its interpreter loops ({loops} found)");
    failures.truncate(20);
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}
