//! Single-producer, single-consumer ring for telemetry samples.
//!
//! The engine's sampling thread writes; the managed host drains. When the host stalls the ring
//! overwrites rather than blocking, because stalling the measurement to preserve a chart is
//! exactly backwards - but an overwrite that goes unreported is worse than either. Every drain
//! reports how many samples it missed, so a gap in the history is visible rather than silently
//! compressed into a continuous-looking line.

use std::sync::atomic::{AtomicU64, Ordering};

use crate::TelemetrySample;

/// Power of two so the modulo is a mask. 1024 samples at 60 Hz is about 17 seconds of history -
/// far longer than any host pause that is not already a bug.
const CAPACITY: usize = 1024;
const MASK: u64 = (CAPACITY as u64) - 1;

pub struct TelemetryRing {
    slots: Box<[TelemetrySample]>,
    /// Total ever written. Monotonic; at 60 Hz a u64 outlasts the hardware by a wide margin.
    written: AtomicU64,
    /// Total ever handed to the consumer, including samples skipped because they were overwritten.
    read: AtomicU64,
}

/// What a drain produced.
pub struct Drained {
    /// Samples copied into the caller's buffer.
    pub count: usize,
    /// Samples the producer overwrote before the consumer reached them. Non-zero means the history
    /// has a hole at this point, and the consumer must not draw across it as though it were
    /// continuous.
    pub dropped: u64,
}

impl Default for TelemetryRing {
    fn default() -> Self {
        Self::new()
    }
}

impl TelemetryRing {
    pub fn new() -> Self {
        Self {
            slots: vec![TelemetrySample::default(); CAPACITY].into_boxed_slice(),
            written: AtomicU64::new(0),
            read: AtomicU64::new(0),
        }
    }

    /// Producer side. Never blocks and never fails; the oldest unread sample is overwritten.
    ///
    /// # Safety of the ordering
    /// The slot is written before `written` is published with Release, and the consumer reads
    /// `written` with Acquire before touching the slot. That pairing is what makes the sample the
    /// consumer sees fully constructed rather than half-written.
    pub fn push(&self, sample: TelemetrySample) {
        let index = self.written.load(Ordering::Relaxed);

        // Only the producer writes slots, so this aliasing is sound for a single producer. The
        // type is Copy and 64 bytes, so the write cannot be observed torn by an Acquire consumer.
        unsafe {
            let slots = self.slots.as_ptr() as *mut TelemetrySample;
            std::ptr::write(slots.add((index & MASK) as usize), sample);
        }

        self.written.store(index + 1, Ordering::Release);
    }

    /// Consumer side. Copies what is available into `out` and reports what was missed.
    ///
    /// Copying rather than lending a view into the ring: at 60 Hz and 64 bytes a sample this is
    /// four kilobytes a second, which is not worth one lifetime hazard across an FFI boundary.
    pub fn drain(&self, out: &mut [TelemetrySample]) -> Drained {
        let written = self.written.load(Ordering::Acquire);
        let mut read = self.read.load(Ordering::Relaxed);

        // Anything more than CAPACITY behind has been overwritten and is gone.
        let dropped = written.saturating_sub(read).saturating_sub(CAPACITY as u64);
        if dropped > 0 {
            read = written - CAPACITY as u64;
        }

        let available = (written - read) as usize;
        let count = available.min(out.len());

        for (i, slot) in out.iter_mut().enumerate().take(count) {
            *slot = self.slots[((read + i as u64) & MASK) as usize];
        }

        self.read.store(read + count as u64, Ordering::Relaxed);
        Drained { count, dropped }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(n: i64) -> TelemetrySample {
        TelemetrySample {
            timestamp_ticks: n,
            ..Default::default()
        }
    }

    #[test]
    fn drains_in_order() {
        let ring = TelemetryRing::new();
        for n in 0..5 {
            ring.push(sample(n));
        }

        let mut out = [TelemetrySample::default(); 8];
        let drained = ring.drain(&mut out);

        assert_eq!(drained.count, 5);
        assert_eq!(drained.dropped, 0);
        assert_eq!(out[0].timestamp_ticks, 0);
        assert_eq!(out[4].timestamp_ticks, 4);
    }

    #[test]
    fn a_second_drain_sees_only_what_is_new() {
        let ring = TelemetryRing::new();
        let mut out = [TelemetrySample::default(); 8];

        ring.push(sample(1));
        assert_eq!(ring.drain(&mut out).count, 1);
        assert_eq!(ring.drain(&mut out).count, 0);

        ring.push(sample(2));
        let drained = ring.drain(&mut out);
        assert_eq!(drained.count, 1);
        assert_eq!(out[0].timestamp_ticks, 2);
    }

    /// The property the review asked for: an overwritten window must be reported, not hidden. A
    /// consumer that cannot tell a gap from continuity will draw a line straight across it.
    #[test]
    fn overwritten_samples_are_reported_as_dropped() {
        let ring = TelemetryRing::new();
        for n in 0..(CAPACITY as i64 + 100) {
            ring.push(sample(n));
        }

        let mut out = [TelemetrySample::default(); CAPACITY];
        let drained = ring.drain(&mut out);

        assert_eq!(drained.dropped, 100, "100 samples were overwritten unread");
        assert_eq!(drained.count, CAPACITY);
        // The oldest surviving sample, not the oldest ever written.
        assert_eq!(out[0].timestamp_ticks, 100);
    }

    #[test]
    fn a_partial_drain_leaves_the_rest() {
        let ring = TelemetryRing::new();
        for n in 0..10 {
            ring.push(sample(n));
        }

        let mut small = [TelemetrySample::default(); 4];
        assert_eq!(ring.drain(&mut small).count, 4);
        assert_eq!(small[0].timestamp_ticks, 0);

        assert_eq!(ring.drain(&mut small).count, 4);
        assert_eq!(small[0].timestamp_ticks, 4);
    }
}
