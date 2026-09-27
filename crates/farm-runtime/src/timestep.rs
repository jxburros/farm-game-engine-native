//! Fixed-timestep accumulator (port of `FixedTimestep.cs` / engine-runtime `fixedTimestep`).
//! The simulation runs at [`TICKS_PER_SECOND`]; rendering interpolates between ticks with
//! [`FixedTimestep::alpha`].

/// Simulation rate in ticks per second (`Engine.TicksPerSecond`).
pub const TICKS_PER_SECOND: f64 = 20.0;

/// Milliseconds per simulation tick.
pub const MS_PER_TICK: f64 = 1000.0 / TICKS_PER_SECOND;

/// Frames longer than this are clamped so a paused window does not spiral (in seconds).
pub const MAX_FRAME_SECONDS: f64 = 0.25;

/// Accumulates real time and hands out whole simulation ticks.
#[derive(Debug, Clone, Default)]
pub struct FixedTimestep {
    accumulator: f64,
}

impl FixedTimestep {
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds `delta_seconds` of frame time and returns the number of ticks to simulate.
    pub fn advance(&mut self, delta_seconds: f64) -> u32 {
        let delta = delta_seconds.clamp(0.0, MAX_FRAME_SECONDS);
        self.accumulator += delta * 1000.0;
        let ticks = (self.accumulator / MS_PER_TICK).floor();
        self.accumulator -= ticks * MS_PER_TICK;
        ticks as u32
    }

    /// Interpolation alpha in [0, 1): how far the next tick is into the accumulator.
    pub fn alpha(&self) -> f64 {
        self.accumulator / MS_PER_TICK
    }

    /// Drops accumulated time (scene changes, focus loss).
    pub fn reset(&mut self) {
        self.accumulator = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sixty_hz_frames_yield_twenty_ticks_per_second() {
        let mut ts = FixedTimestep::new();
        let mut ticks = 0;
        for _ in 0..60 {
            ticks += ts.advance(1.0 / 60.0);
        }
        assert_eq!(ticks, 20);
    }

    #[test]
    fn long_frames_are_clamped() {
        let mut ts = FixedTimestep::new();
        assert_eq!(ts.advance(10.0), 5);
    }
}
