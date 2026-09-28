//! Timing report for the plugin sandbox (wall clock, so it only prints; nothing is asserted
//! about time). Run it in release mode:
//!
//! ```sh
//! cargo test -p farm-plugins --release --test perf -- --ignored --nocapture
//! ```
#![allow(clippy::disallowed_types)] // std::time::Instant: this test measures wall-clock time.

mod common;

use common::{plugin, DAY3};
use farm_plugins::{plugin_specs_from_packs, PluginHost, PluginHostOptions, WasmPluginHost};
use farm_sim::hooks::hook_names;
use std::time::Instant;

const TRIVIAL: &str = "api.on('onDayStart', p => [{ type: 'message', text: 'day ' + p.day }])";

fn host(options: PluginHostOptions, sources: &[&str]) -> WasmPluginHost {
    let plugins = sources.iter().enumerate().map(|(i, source)| plugin(&format!("p{i}"), source, &[])).collect();
    WasmPluginHost::new(plugin_specs_from_packs([&common::pack("pack-a", hook_names::ALL, plugins)]), options)
}

fn unlimited() -> PluginHostOptions {
    PluginHostOptions { fuel_per_call: 1 << 50, ..PluginHostOptions::default() }
}

/// Fuel and seconds of one warm call.
fn measure(source: &str) -> (u64, f64) {
    let mut host = host(unlimited(), &[source]);
    assert!(host.init_errors().is_empty(), "{:?}", host.init_errors());
    let first = host.dispatch("onDayStart", DAY3);
    assert!(first.iter().all(|r| r.errors.is_empty()), "{first:?}");
    let started = Instant::now();
    host.dispatch("onDayStart", DAY3);
    (host.last_fuel_used("pack-a:p0").expect("measured"), started.elapsed().as_secs_f64())
}

#[test]
#[ignore = "timing report; run with --release --ignored --nocapture"]
fn report() {
    let started = Instant::now();
    let one = host(PluginHostOptions::default(), &[TRIVIAL]);
    let first_host = started.elapsed();
    let init_fuel = one.last_fuel_used("pack-a:p0").expect("measured");
    let started = Instant::now();
    let _ten = host(PluginHostOptions::default(), &[TRIVIAL; 10]);
    let ten_hosts = started.elapsed();
    println!("host with 1 plugin (compile + prepare + init): {first_host:.1?}; with 10 plugins: {ten_hosts:.1?}");
    println!("init of a small plugin: {init_fuel} fuel, ~{:.2?} each", (ten_hosts - first_host) / 9);

    let mut trivial = host(PluginHostOptions::default(), &[TRIVIAL]);
    trivial.dispatch("onDayStart", DAY3);
    let started = Instant::now();
    for _ in 0..1000 {
        trivial.dispatch("onDayStart", DAY3);
    }
    let per_dispatch = started.elapsed() / 1000;
    let fuel = trivial.last_fuel_used("pack-a:p0").expect("measured");
    println!("trivial handler: {fuel} fuel, {per_dispatch:.1?} per dispatch");

    for n in [1_000u64, 10_000, 100_000] {
        let (fuel, secs) = measure(&format!(
            "api.on('onDayStart', function () {{ var s = 0; for (var i = 0; i < {n}; i++) s += i; return [{{ type: 'message', text: String(s) }}]; }})"
        ));
        println!(
            "loop of {n}: {fuel} fuel ({} per iteration), {:.2} ms, {:.0} M fuel/s",
            fuel / n,
            secs * 1e3,
            fuel as f64 / secs / 1e6
        );
    }
    let native = [
        ("indexOf over 1 MB x5", "var s = 'x'.repeat(1000000); api.on('onDayStart', function () { var n = 0; for (var i = 0; i < 5; i++) n += s.indexOf('y'); return [{ type: 'message', text: String(n) }]; })"),
        ("JSON.stringify 20k objects", "var o = []; for (var i = 0; i < 20000; i++) o.push({ a: i, b: 'text' + i }); api.on('onDayStart', function () { return [{ type: 'message', text: String(JSON.stringify(o).length) }]; })"),
        ("sort 20k numbers", "api.on('onDayStart', function () { var a = []; for (var i = 0; i < 20000; i++) a.push((i * 7919) % 20000); a.sort(); return [{ type: 'message', text: String(a[5]) }]; })"),
        ("regex over 40 kB x5", "var s = 'ab'.repeat(20000); api.on('onDayStart', function () { var n = 0; for (var i = 0; i < 5; i++) n += (s.match(/a/g) || []).length; return [{ type: 'message', text: String(n) }]; })"),
    ];
    for (name, source) in native {
        let (fuel, secs) = measure(source);
        println!("{name}: {fuel} fuel, {:.2} ms, {:.0} M fuel/s", secs * 1e3, fuel as f64 / secs / 1e6);
    }

    let mut looping = host(PluginHostOptions::default(), &["api.on('onDayStart', function () { while (true) {} })"]);
    let started = Instant::now();
    let result = looping.dispatch("onDayStart", DAY3);
    println!(
        "infinite loop at the default {} fuel: stopped after {:.1?} (including the rebuild): {}",
        looping.options().fuel_per_call,
        started.elapsed(),
        result[0].errors[0].message
    );
}
