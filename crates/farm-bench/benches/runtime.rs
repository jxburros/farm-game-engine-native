//! Criterion benchmarks of the runtime scenarios (see the crate docs):
//! `cargo bench -p farm-bench [-- FILTER]`.
//!
//! The quick CI budget check is the `farm-bench` binary (`cargo run --release -p farm-bench --
//! --budget`), which runs the same scenarios without criterion's long sampling.

use criterion::{criterion_group, criterion_main, BatchSize, Criterion, Throughput};
use farm_bench::{
    farm, gameplay_player, load_cartridge, npc_ticks, npc_town, render_frame, sample_cartridges, sleep,
    start_cartridge, FarmSpec, SaveFixture,
};
use std::hint::black_box;
use std::time::Duration;

fn overnight(c: &mut Criterion) {
    let mut group = c.benchmark_group("overnight");
    for (name, spec, samples) in [
        ("64x64 full crops", FarmSpec::OVERNIGHT_64, 100),
        ("256x256 full crops + 50 machines", FarmSpec::OVERNIGHT_256, 20),
    ] {
        let farm = farm(spec);
        group.sample_size(samples);
        group.throughput(Throughput::Elements((spec.size * spec.size) as u64));
        // The state is cloned outside the timed part; each run sleeps from the same evening.
        group.bench_function(name, |b| {
            b.iter_batched(
                || farm.state.clone(),
                |mut state| {
                    black_box(sleep(&farm.ctx, &mut state));
                    state
                },
                BatchSize::LargeInput,
            )
        });
    }
    group.finish();
}

fn save(c: &mut Criterion) {
    let save = SaveFixture::mid_size();
    let binary = save.write_binary();
    let json = save.write_json();
    let mut group = c.benchmark_group("save");
    group.bench_function("stable json (sim thread)", |b| b.iter(|| save.stable_json()));
    group.bench_function("binary FGSV", |b| b.iter(|| save.write_binary()));
    group.bench_function("json", |b| b.iter(|| save.write_json()));
    group.bench_function("load binary FGSV", |b| b.iter(|| save.load(black_box(&binary))));
    group.bench_function("load json", |b| b.iter(|| save.load(black_box(json.as_bytes()))));
    group.bench_function("save+load binary FGSV", |b| b.iter(|| save.load(&save.write_binary())));
    group.finish();
}

fn cartridges(c: &mut Criterion) {
    let mut group = c.benchmark_group("cart");
    for (name, bytes) in sample_cartridges() {
        group.throughput(Throughput::Bytes(bytes.len() as u64));
        group.bench_function(format!("load/{name}"), |b| b.iter(|| load_cartridge(black_box(&bytes))));
        group.bench_function(format!("start/{name}"), |b| b.iter(|| start_cartridge(black_box(&bytes))));
    }
    group.finish();
}

fn frames(c: &mut Criterion) {
    let mut group = c.benchmark_group("frame");
    group.sample_size(60).measurement_time(Duration::from_secs(8));
    for (width, height) in [(1280, 800), (1920, 1080)] {
        let mut player = gameplay_player(width, height);
        let mut frame = 30;
        group.bench_function(format!("{width}x{height} cpu"), |b| {
            b.iter(|| {
                render_frame(&mut player, frame, width, height);
                frame += 1;
            })
        });
    }
    group.finish();
}

fn npcs(c: &mut Criterion) {
    let mut group = c.benchmark_group("npcs");
    group.sample_size(10);
    for (name, walled) in [("20 walking 64x64 1000 ticks", false), ("20 walled off 64x64 1000 ticks", true)] {
        let town = npc_town(walled);
        group.bench_function(name, |b| {
            b.iter_batched(
                || town.state.clone(),
                |mut state| {
                    black_box(npc_ticks(&town.ctx, &mut state));
                    state
                },
                BatchSize::LargeInput,
            )
        });
    }
    group.finish();
}

criterion_group!(benches, overnight, save, cartridges, frames, npcs);
criterion_main!(benches);
