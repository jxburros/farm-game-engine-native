#![no_main]
//! `farm_fuzz::Target::PluginMutations` under libFuzzer (see crates/farm-fuzz).

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    farm_fuzz::Target::PluginMutations.run(data);
});
