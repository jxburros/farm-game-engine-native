#![no_main]
//! `farm_fuzz::Target::Image` under libFuzzer (see crates/farm-fuzz).

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    farm_fuzz::Target::Image.run(data);
});
