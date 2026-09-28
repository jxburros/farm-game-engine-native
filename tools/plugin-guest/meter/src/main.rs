//! `farm-plugin-meter IN.wasm OUT.wasm`: adds deterministic instruction metering to the plugin
//! guest.
//!
//! The host gives every plugin call a budget of *fuel* and stops the call when it runs out.
//! wasmi has fuel metering built in, but it charges a whole loop body up front, including the
//! code after every nested `block` end. For an interpreter's `switch` loop (QuickJS's
//! bytecode loop) that is every case at every step, a few hundred times the real cost, while a
//! tight C loop is charged about right. A budget could then not bound time: native-heavy code
//! would get hundreds of times more of it than interpreted code.
//!
//! This pass charges each straight-line stretch of code when it starts, so one unit of fuel is
//! one executed instruction (an upper bound: when a `br_if` leaves a stretch early, the rest of
//! the stretch was already paid for). The counter is an exported mutable global:
//!
//! - `fp_fuel` (`i64`): the host sets it before a call and reads it after.
//! - A stretch starts at a function entry and after every `loop`, `if`, `else` and `end`; it is
//!   charged with `fuel -= cost`.
//! - Loop headers and the entries of functions that call others also check the counter and hit
//!   `unreachable` when it is negative. Every loop iteration and every call (so every
//!   recursion) passes a check, so a call that runs out of fuel stops after at most one
//!   straight-line stretch more.
//! - `memory.copy`, `memory.fill` and `memory.init` do work proportional to their length in
//!   one instruction; they also pay one unit per 8 bytes, through a helper function appended
//!   to the module.
//!
//! `block`, `loop`, `end`, `else` and `nop` cost nothing; every other instruction costs 1.
//! The metering code itself is not charged. New items are appended (a type, a function, a
//! global, an export), so no existing index changes.

use std::process::ExitCode;
use wasm_encoder::reencode::{Error, Reencode};
use wasm_encoder::{BlockType, ConstExpr, ExportKind, Function, GlobalType, Instruction, ValType};
use wasmparser::{Operator, Parser, Payload};

/// Name of the exported fuel counter.
const FUEL_EXPORT: &str = "fp_fuel";
/// Bytes of bulk memory work per unit of fuel.
const BULK_BYTES_PER_FUEL_SHIFT: i64 = 3;

/// Index-space facts the instrumentation needs, from a first pass over the module.
#[derive(Debug, Default)]
struct Layout {
    types: u32,
    imported_functions: u32,
    defined_functions: u32,
    imported_globals: u32,
    defined_globals: u32,
    has_global_section: bool,
    has_export_section: bool,
}

impl Layout {
    fn scan(wasm: &[u8]) -> Result<Self, String> {
        let mut layout = Self::default();
        for payload in Parser::new(0).parse_all(wasm) {
            match payload.map_err(|e| e.to_string())? {
                Payload::TypeSection(section) => {
                    for group in section {
                        let group = group.map_err(|e| e.to_string())?;
                        if group.is_explicit_rec_group() {
                            return Err("explicit rec groups are not supported".to_owned());
                        }
                        layout.types += group.types().len() as u32;
                    }
                }
                Payload::ImportSection(section) => {
                    for import in section.into_imports() {
                        match import.map_err(|e| e.to_string())?.ty {
                            wasmparser::TypeRef::Func(_) | wasmparser::TypeRef::FuncExact(_) => {
                                layout.imported_functions += 1
                            }
                            wasmparser::TypeRef::Global(_) => layout.imported_globals += 1,
                            _ => {}
                        }
                    }
                }
                Payload::FunctionSection(section) => layout.defined_functions = section.count(),
                Payload::GlobalSection(section) => {
                    layout.has_global_section = true;
                    layout.defined_globals = section.count();
                }
                Payload::ExportSection(_) => layout.has_export_section = true,
                _ => {}
            }
        }
        if !layout.has_global_section || !layout.has_export_section {
            return Err("the module needs a global and an export section".to_owned());
        }
        Ok(layout)
    }

    fn helper_type(&self) -> u32 {
        self.types
    }

    fn helper_function(&self) -> u32 {
        self.imported_functions + self.defined_functions
    }

    fn fuel_global(&self) -> u32 {
        self.imported_globals + self.defined_globals
    }
}

struct Meter {
    layout: Layout,
    stats: Stats,
}

#[derive(Debug, Default)]
struct Stats {
    functions: u32,
    charges: u32,
    checks: u32,
    bulk: u32,
}

/// Where to insert metering before an operator.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
struct Charge {
    cost: u64,
    check: bool,
}

fn cost(op: &Operator<'_>) -> u64 {
    match op {
        Operator::Block { .. } | Operator::Loop { .. } | Operator::End | Operator::Else | Operator::Nop => 0,
        _ => 1,
    }
}

fn is_bulk(op: &Operator<'_>) -> bool {
    matches!(op, Operator::MemoryCopy { .. } | Operator::MemoryFill { .. } | Operator::MemoryInit { .. })
}

/// The charges of one function body: `plan[i]` is inserted before operator `i`.
fn plan(ops: &[Operator<'_>]) -> Vec<Option<Charge>> {
    let calls = ops.iter().any(|op| {
        matches!(
            op,
            Operator::Call { .. }
                | Operator::CallIndirect { .. }
                | Operator::ReturnCall { .. }
                | Operator::ReturnCallIndirect { .. }
                | Operator::CallRef { .. }
                | Operator::ReturnCallRef { .. }
        )
    });
    let mut plan = vec![None; ops.len()];
    // The open stretch: where its charge goes, what it costs so far, whether it checks.
    let mut start = 0usize;
    let mut open = Charge { cost: 0, check: calls };
    let last = ops.len().saturating_sub(1);
    fn close(start: usize, charge: Charge, plan: &mut [Option<Charge>]) {
        if (charge.cost > 0 || charge.check) && start < plan.len() {
            plan[start] = Some(charge);
        }
    }
    for (index, op) in ops.iter().enumerate() {
        open.cost += cost(op);
        let next = match op {
            Operator::Loop { .. } => Some(true),
            Operator::If { .. } | Operator::Else | Operator::End if index != last => Some(false),
            _ => None,
        };
        if let Some(check) = next {
            close(start, open, &mut plan);
            start = index + 1;
            open = Charge { cost: 0, check };
        }
    }
    close(start, open, &mut plan);
    plan
}

impl Meter {
    fn emit_charge(&mut self, function: &mut Function, charge: Charge) {
        let fuel = self.layout.fuel_global();
        if charge.cost > 0 {
            self.stats.charges += 1;
            function.instruction(&Instruction::GlobalGet(fuel));
            function.instruction(&Instruction::I64Const(charge.cost as i64));
            function.instruction(&Instruction::I64Sub);
            function.instruction(&Instruction::GlobalSet(fuel));
        }
        if charge.check {
            self.stats.checks += 1;
            emit_check(function, fuel);
        }
    }

    /// `(func (param $len i32) (result i32))`: pay for `$len` bytes of bulk memory work, check,
    /// and hand the length back.
    fn helper(&self) -> Function {
        let fuel = self.layout.fuel_global();
        let mut function = Function::new([]);
        function.instruction(&Instruction::GlobalGet(fuel));
        function.instruction(&Instruction::LocalGet(0));
        function.instruction(&Instruction::I64ExtendI32U);
        function.instruction(&Instruction::I64Const(BULK_BYTES_PER_FUEL_SHIFT));
        function.instruction(&Instruction::I64ShrU);
        function.instruction(&Instruction::I64Sub);
        function.instruction(&Instruction::GlobalSet(fuel));
        emit_check(&mut function, fuel);
        function.instruction(&Instruction::LocalGet(0));
        function.instruction(&Instruction::End);
        function
    }
}

/// `if (fuel < 0) unreachable`
fn emit_check(function: &mut Function, fuel: u32) {
    function.instruction(&Instruction::GlobalGet(fuel));
    function.instruction(&Instruction::I64Const(0));
    function.instruction(&Instruction::I64LtS);
    function.instruction(&Instruction::If(BlockType::Empty));
    function.instruction(&Instruction::Unreachable);
    function.instruction(&Instruction::End);
}

impl Reencode for Meter {
    type Error = String;

    fn parse_type_section(
        &mut self,
        types: &mut wasm_encoder::TypeSection,
        section: wasmparser::TypeSectionReader<'_>,
    ) -> Result<(), Error<Self::Error>> {
        wasm_encoder::reencode::utils::parse_type_section(self, types, section)?;
        types.ty().function([ValType::I32], [ValType::I32]);
        Ok(())
    }

    fn parse_function_section(
        &mut self,
        functions: &mut wasm_encoder::FunctionSection,
        section: wasmparser::FunctionSectionReader<'_>,
    ) -> Result<(), Error<Self::Error>> {
        wasm_encoder::reencode::utils::parse_function_section(self, functions, section)?;
        functions.function(self.layout.helper_type());
        Ok(())
    }

    fn parse_global_section(
        &mut self,
        globals: &mut wasm_encoder::GlobalSection,
        section: wasmparser::GlobalSectionReader<'_>,
    ) -> Result<(), Error<Self::Error>> {
        wasm_encoder::reencode::utils::parse_global_section(self, globals, section)?;
        globals.global(GlobalType { val_type: ValType::I64, mutable: true, shared: false }, &ConstExpr::i64_const(0));
        Ok(())
    }

    fn parse_export_section(
        &mut self,
        exports: &mut wasm_encoder::ExportSection,
        section: wasmparser::ExportSectionReader<'_>,
    ) -> Result<(), Error<Self::Error>> {
        wasm_encoder::reencode::utils::parse_export_section(self, exports, section)?;
        exports.export(FUEL_EXPORT, ExportKind::Global, self.layout.fuel_global());
        Ok(())
    }

    fn parse_code_section(
        &mut self,
        code: &mut wasm_encoder::CodeSection,
        section: wasmparser::CodeSectionReader<'_>,
    ) -> Result<(), Error<Self::Error>> {
        wasm_encoder::reencode::utils::parse_code_section(self, code, section)?;
        code.function(&self.helper());
        Ok(())
    }

    fn parse_function_body(
        &mut self,
        code: &mut wasm_encoder::CodeSection,
        body: wasmparser::FunctionBody<'_>,
    ) -> Result<(), Error<Self::Error>> {
        self.stats.functions += 1;
        let mut function = self.new_function_with_parsed_locals(&body)?;
        let mut reader = body.get_operators_reader()?;
        let mut ops = Vec::new();
        while !reader.eof() {
            ops.push(reader.read()?);
        }
        let plan = plan(&ops);
        let helper = self.layout.helper_function();
        for (op, charge) in ops.into_iter().zip(plan) {
            if let Some(charge) = charge {
                self.emit_charge(&mut function, charge);
            }
            if is_bulk(&op) {
                self.stats.bulk += 1;
                function.instruction(&Instruction::Call(helper));
            }
            function.instruction(&self.instruction(op)?);
        }
        code.function(&function);
        Ok(())
    }
}

fn run(input: &str, output: &str) -> Result<(), String> {
    let wasm = std::fs::read(input).map_err(|e| format!("read {input}: {e}"))?;
    let layout = Layout::scan(&wasm)?;
    let mut meter = Meter { layout, stats: Stats::default() };
    let mut module = wasm_encoder::Module::new();
    meter.parse_core_module(&mut module, Parser::new(0), &wasm).map_err(|e| format!("{e:?}"))?;
    let metered = module.finish();
    wasmparser::Validator::new().validate_all(&metered).map_err(|e| format!("metered module is invalid: {e}"))?;
    std::fs::write(output, &metered).map_err(|e| format!("write {output}: {e}"))?;
    eprintln!(
        "metered {} functions: {} charges, {} checks, {} bulk-memory sites; {} -> {} bytes",
        meter.stats.functions,
        meter.stats.charges,
        meter.stats.checks,
        meter.stats.bulk,
        wasm.len(),
        metered.len()
    );
    Ok(())
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();
    let [_, input, output] = args.as_slice() else {
        eprintln!("usage: farm-plugin-meter IN.wasm OUT.wasm");
        return ExitCode::FAILURE;
    };
    match run(input, output) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("farm-plugin-meter: {error}");
            ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ops(wat_ops: &[Operator<'static>]) -> Vec<Option<Charge>> {
        plan(wat_ops)
    }

    #[test]
    fn charges_each_stretch_where_it_starts() {
        use Operator::*;
        // (func i32.const 1 drop block i32.const 2 br_if 0 i32.const 3 drop end i32.const 4 drop end)
        let body = [
            I32Const { value: 1 },
            Drop,
            Block { blockty: wasmparser::BlockType::Empty },
            I32Const { value: 2 },
            BrIf { relative_depth: 0 },
            I32Const { value: 3 },
            Drop,
            End,
            I32Const { value: 4 },
            Drop,
            End,
        ];
        let plan = ops(&body);
        // Entry stretch: const, drop, const, br_if, const, drop = 6 (block and end are free).
        assert_eq!(plan[0], Some(Charge { cost: 6, check: false }));
        // After the block's end: const, drop = 2.
        assert_eq!(plan[8], Some(Charge { cost: 2, check: false }));
        assert_eq!(plan.iter().filter(|c| c.is_some()).count(), 2);
    }

    #[test]
    fn loops_and_calling_functions_check() {
        use Operator::*;
        let body = [
            Call { function_index: 0 },
            Loop { blockty: wasmparser::BlockType::Empty },
            I32Const { value: 1 },
            BrIf { relative_depth: 0 },
            End,
            End,
        ];
        let plan = ops(&body);
        assert_eq!(plan[0], Some(Charge { cost: 1, check: true }));
        assert_eq!(plan[2], Some(Charge { cost: 2, check: true }));
    }
}
