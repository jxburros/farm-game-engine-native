//! `farm-bench`: times every scenario a fixed number of times and prints the medians.
//!
//! ```text
//! cargo run --release -p farm-bench -- [--budget] [--factor F] [--scale S] [FILTER]...
//! ```
//!
//! - `--budget`: exit with status 1 when a median is over its limit, the budget × the factor
//!   (CI). A budget the code is known to miss has a ceiling at today's time instead, so CI
//!   still catches regressions; the table reports it as over budget.
//! - `--factor F`: the budget multiplier (default 3: shared CI runners are slower and noisier
//!   than the mid-range laptop the budgets are written for).
//! - `--scale S`: multiplies the run counts (default 1).
//! - `FILTER`: run only the scenarios whose name contains one of these substrings.
//!
//! For statistics (confidence intervals, comparison with the last run) use criterion:
//! `cargo bench -p farm-bench`.
#![forbid(unsafe_code)]

use farm_bench::{scenarios, Measurement};
use std::process::ExitCode;

const USAGE: &str = "usage: farm-bench [--budget] [--factor F] [--scale S] [FILTER]...";

#[derive(Debug)]
struct Args {
    budget: bool,
    factor: f64,
    scale: f64,
    filters: Vec<String>,
}

fn parse_args() -> Result<Args, String> {
    let mut args = Args { budget: false, factor: 3.0, scale: 1.0, filters: Vec::new() };
    let mut iter = std::env::args().skip(1);
    let number = |flag: &str, value: Option<String>| -> Result<f64, String> {
        value
            .and_then(|v| v.parse::<f64>().ok())
            .filter(|v| v.is_finite() && *v > 0.0)
            .ok_or_else(|| format!("{flag} needs a positive number"))
    };
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--budget" => args.budget = true,
            "--factor" => args.factor = number("--factor", iter.next())?,
            "--scale" => args.scale = number("--scale", iter.next())?,
            "-h" | "--help" => return Err(String::new()),
            flag if flag.starts_with('-') => return Err(format!("unknown option {flag}")),
            filter => args.filters.push(filter.to_owned()),
        }
    }
    Ok(args)
}

fn main() -> ExitCode {
    let args = match parse_args() {
        Ok(args) => args,
        Err(message) => {
            if !message.is_empty() {
                eprintln!("{message}");
            }
            eprintln!("{USAGE}");
            return if message.is_empty() { ExitCode::SUCCESS } else { ExitCode::from(2) };
        }
    };

    let selected: Vec<_> = scenarios(args.scale)
        .into_iter()
        .filter(|s| args.filters.is_empty() || args.filters.iter().any(|f| s.name.contains(f.as_str())))
        .collect();
    if selected.is_empty() {
        eprintln!("no scenario matches {:?}", args.filters);
        return ExitCode::from(2);
    }
    if cfg!(debug_assertions) {
        eprintln!("warning: a debug build; budgets are meant for --release");
    }

    println!(
        "{:<46} {:>5} {:>10} {:>10} {:>10} {:>10}  status",
        "scenario", "runs", "median ms", "min ms", "budget ms", "limit ms"
    );
    let mut over: Vec<Measurement> = Vec::new();
    for scenario in selected {
        let runs = scenario.runs;
        let measurement = scenario.measure(runs);
        let budget = measurement.budget_ms.map_or_else(|| "-".to_owned(), |ms| format!("{ms:.2}"));
        let limit = measurement.limit_ms(args.factor).map_or_else(|| "-".to_owned(), |ms| format!("{ms:.2}"));
        let status = if !measurement.within(args.factor) {
            "OVER LIMIT"
        } else if !measurement.meets_budget() {
            "over budget, within limit"
        } else if measurement.budget_ms.is_some() {
            "ok"
        } else {
            ""
        };
        println!(
            "{:<46} {:>5} {:>10.3} {:>10.3} {:>10} {:>10}  {status}",
            measurement.name,
            measurement.samples.len(),
            measurement.median_ms,
            measurement.min_ms(),
            budget,
            limit
        );
        if !measurement.within(args.factor) {
            over.push(measurement);
        }
    }

    if args.budget && !over.is_empty() {
        eprintln!();
        for m in &over {
            eprintln!(
                "{}: median {:.3} ms is over the limit {:.2} ms × {}",
                m.name,
                m.median_ms,
                m.enforced_ms.unwrap_or_default(),
                args.factor
            );
        }
        return ExitCode::FAILURE;
    }
    ExitCode::SUCCESS
}
