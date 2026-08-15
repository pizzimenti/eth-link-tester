//! Single-producer, single-consumer ring for telemetry samples.
//!
//! The engine's sampling thread writes; the managed host drains. When the host stalls the ring
//! overwrites rather than blocking, because stalling the measurement to preserve a chart is
//! exactly backwards - but an overwrite that goes unreported is worse than either. Every drain
//! reports how many samples it missed, so a gap in the history is visible rather than silently
//! compressed into a continuous-looking line.
//!
//! # Why each slot carries a stamp
//!
//! Publishing an index with Release and reading it with Acquire orders *that* write against *that*
//! read, and orders nothing at all against a later overwrite of the same slot. A producer wrapping
//! the ring can be rewriting slot `n` while the consumer is still copying it, which yields a
//! sample assembled from two different writes - measured at 164,856 torn samples in a three-second
//! race, alongside 7,184 gaps the old `dropped` arithmetic never reported because it sampled the
//! write index before the copy rather than after.
//!
//! Each slot therefore carries a stamp that the consumer checks before and after copying. A slot
//! whose stamp changed underneath the copy is discarded rather than delivered, and the drain
//! recomputes what it missed from the index as it stands when the copying is finished.

use std::cell::UnsafeCell;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::TelemetrySample;

/// Power of two so the modulo is a mask. 1024 samples at 60 Hz is about 17 seconds of history -
/// far longer than any host pause that is not already a bug.
const CAPACITY: usize = 1024;
const MASK: u64 = (CAPACITY as u64) - 1;

/// A slot and the position it currently holds, or 0 while it is being written.
struct Slot {
    /// `position + 1` once written, so 0 is unambiguously "never written or being written".
    stamp: AtomicU64,
    value: UnsafeCell<TelemetrySample>,
}

pub struct TelemetryRing {
    slots: Box<[Slot]>,
    /// Total ever written. Monotonic; at 60 Hz a u64 outlasts the hardware by a wide margin.
    written: AtomicU64,
    /// Total the consumer has accounted for, delivered or skipped.
    read: AtomicU64,
}

/// Sharing is sound because the access rules are enforced by the `unsafe` contracts on
/// [`TelemetryRing::push`] and [`TelemetryRing::drain`]: one producer, one consumer.
unsafe impl Sync for TelemetryRing {}
unsafe impl Send for TelemetryRing {}

/// What a drain produced.
pub struct Drained {
    /// Samples copied into the caller's buffer.
    pub count: usize,
    /// Samples the producer overwrote before the consumer reached them, including any overwritten
    /// while this drain was copying. Non-zero means the history has a hole at this point, and the
    /// consumer must not draw across it as though it were continuous.
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
            slots: (0..CAPACITY)
                .map(|_| Slot {
                    stamp: AtomicU64::new(0),
                    value: UnsafeCell::new(TelemetrySample::default()),
                })
                .collect::<Vec<_>>()
                .into_boxed_slice(),
            written: AtomicU64::new(0),
            read: AtomicU64::new(0),
        }
    }

    /// Publishes a sample. Never blocks and never fails; the oldest unread sample is overwritten.
    ///
    /// # Safety
    /// Exactly one thread may call this for the lifetime of the ring. Two concurrent calls are a
    /// data race on the slot contents. This is `unsafe` rather than merely documented because the
    /// previous safe signature made that race reachable without a single `unsafe` block at the
    /// call site - 24 million torn samples in a two-producer test that the compiler accepted
    /// without complaint.
    pub unsafe fn push(&self, sample: TelemetrySample) {
        let position = self.written.load(Ordering::Relaxed);
        let slot = &self.slots[(position & MASK) as usize];

        // Zero first so a consumer copying this slot sees the stamp change and discards what it
        // read, rather than assembling a sample from the old value and the new one.
        slot.stamp.store(0, Ordering::Release);

        unsafe { std::ptr::write(slot.value.get(), sample) };

        slot.stamp.store(position + 1, Ordering::Release);
        self.written.store(position + 1, Ordering::Release);
    }

    /// Copies what is available into `out` and reports what was missed.
    ///
    /// Copying rather than lending a view into the ring: at 60 Hz and 64 bytes a sample this is
    /// four kilobytes a second, which is not worth one lifetime hazard across an FFI boundary.
    ///
    /// # Safety
    /// Exactly one thread may call this at a time. Two concurrent drains would each advance the
    /// read cursor and deliver overlapping samples - 191 million duplicates in a two-consumer
    /// test. The FFI layer enforces this with a state word rather than trusting the caller.
    pub unsafe fn drain(&self, out: &mut [TelemetrySample]) -> Drained {
        let mut read = self.read.load(Ordering::Relaxed);
        let written = self.written.load(Ordering::Acquire);

        // Anything more than CAPACITY behind has already been overwritten.
        let mut dropped = written.saturating_sub(read).saturating_sub(CAPACITY as u64);
        read += dropped;

        let mut count = 0usize;
        while count < out.len() && read < written {
            let slot = &self.slots[(read & MASK) as usize];

            let before = slot.stamp.load(Ordering::Acquire);
            let value = unsafe { std::ptr::read(slot.value.get()) };
            let after = slot.stamp.load(Ordering::Acquire);

            if before == read + 1 && after == before {
                out[count] = value;
                count += 1;
            } else {
                // The producer moved this slot on while it was being copied, so the sample is
                // either torn or already superseded. Either way it is lost, and saying so is the
                // entire point of the mechanism.
                dropped += 1;
            }

            read += 1;
        }

        self.read.store(read, Ordering::Relaxed);
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

    fn push(ring: &TelemetryRing, n: i64) {
        // Single-threaded in tests, so the contract holds.
        unsafe { ring.push(sample(n)) };
    }

    fn drain(ring: &TelemetryRing, out: &mut [TelemetrySample]) -> Drained {
        unsafe { ring.drain(out) }
    }

    #[test]
    fn drains_in_order() {
        let ring = TelemetryRing::new();
        for n in 0..5 {
            push(&ring, n);
        }

        let mut out = [TelemetrySample::default(); 8];
        let drained = drain(&ring, &mut out);

        assert_eq!(drained.count, 5);
        assert_eq!(drained.dropped, 0);
        assert_eq!(out[0].timestamp_ticks, 0);
        assert_eq!(out[4].timestamp_ticks, 4);
    }

    #[test]
    fn a_second_drain_sees_only_what_is_new() {
        let ring = TelemetryRing::new();
        let mut out = [TelemetrySample::default(); 8];

        push(&ring, 1);
        assert_eq!(drain(&ring, &mut out).count, 1);
        assert_eq!(drain(&ring, &mut out).count, 0);

        push(&ring, 2);
        let drained = drain(&ring, &mut out);
        assert_eq!(drained.count, 1);
        assert_eq!(out[0].timestamp_ticks, 2);
    }

    /// An overwritten window must be reported, not hidden. A consumer that cannot tell a gap from
    /// continuity will draw a line straight across it.
    #[test]
    fn overwritten_samples_are_reported_as_dropped() {
        let ring = TelemetryRing::new();
        for n in 0..(CAPACITY as i64 + 100) {
            push(&ring, n);
        }

        let mut out = [TelemetrySample::default(); CAPACITY];
        let drained = drain(&ring, &mut out);

        assert_eq!(drained.dropped, 100, "100 samples were overwritten unread");
        assert_eq!(drained.count, CAPACITY);
        assert_eq!(out[0].timestamp_ticks, 100);
    }

    #[test]
    fn a_partial_drain_leaves_the_rest() {
        let ring = TelemetryRing::new();
        for n in 0..10 {
            push(&ring, n);
        }

        let mut small = [TelemetrySample::default(); 4];
        assert_eq!(drain(&ring, &mut small).count, 4);
        assert_eq!(small[0].timestamp_ticks, 0);

        assert_eq!(drain(&ring, &mut small).count, 4);
        assert_eq!(small[0].timestamp_ticks, 4);
    }

    /// Every sample is either delivered or counted as dropped. This is the invariant the whole
    /// mechanism exists to provide, and the one the old implementation broke under contention.
    ///
    /// The producer deliberately outruns the consumer - 100 pushed per 64 drained - so the ring
    /// wraps and the drop path is exercised rather than merely present. The tail is then drained to
    /// empty before the sum is checked: samples still sitting in the ring are neither delivered nor
    /// lost, and counting them as missing would assert something the ring never promised.
    #[test]
    fn every_sample_is_delivered_or_reported() {
        let ring = TelemetryRing::new();
        let mut out = [TelemetrySample::default(); 64];

        let mut delivered = 0u64;
        let mut lost = 0u64;
        let mut take = |ring: &TelemetryRing, out: &mut [TelemetrySample]| {
            let d = drain(ring, out);
            delivered += d.count as u64;
            lost += d.dropped;
            d.count
        };

        for n in 0..5_000i64 {
            push(&ring, n);
            if n % 100 == 0 {
                take(&ring, &mut out);
            }
        }
        while take(&ring, &mut out) > 0 {}

        assert!(
            lost > 0,
            "the consumer never fell behind, so this proved nothing"
        );
        assert_eq!(
            delivered + lost,
            5_000,
            "nothing may vanish unaccounted for"
        );
    }

    /// The race the previous implementation lost. A producer at full speed against a consumer
    /// draining in the width the host actually uses: no sample may arrive torn, and the ordering
    /// annotations must be load-bearing enough that this fails without them.
    #[test]
    fn a_concurrent_producer_never_delivers_a_torn_sample() {
        use std::sync::atomic::AtomicBool;
        use std::sync::Arc;

        let ring = Arc::new(TelemetryRing::new());
        let stop = Arc::new(AtomicBool::new(false));

        let producer = {
            let ring = Arc::clone(&ring);
            let stop = Arc::clone(&stop);
            std::thread::spawn(move || {
                let mut n = 0i64;
                while !stop.load(Ordering::Relaxed) {
                    // Every field carries the same value, so any mixture of two writes is visible.
                    unsafe {
                        ring.push(TelemetrySample {
                            timestamp_ticks: n,
                            tx_megabits_per_second: n as f64,
                            rx_megabits_per_second: n as f64,
                            latency_p50_microseconds: n as f64,
                            latency_p99_microseconds: n as f64,
                            tx_frames: n,
                            rx_frames: n,
                            rx_errors: n,
                        })
                    };
                    n += 1;
                }
                n
            })
        };

        let mut out = [TelemetrySample::default(); 512];
        let mut checked = 0u64;
        let deadline = std::time::Instant::now() + std::time::Duration::from_millis(750);

        while std::time::Instant::now() < deadline {
            let drained = unsafe { ring.drain(&mut out) };
            for sample in out.iter().take(drained.count) {
                let n = sample.timestamp_ticks;
                assert_eq!(sample.tx_frames, n, "torn sample: fields disagree");
                assert_eq!(sample.rx_frames, n, "torn sample: fields disagree");
                assert_eq!(sample.rx_errors, n, "torn sample: fields disagree");
                assert_eq!(sample.tx_megabits_per_second, n as f64, "torn sample");
                assert_eq!(sample.latency_p99_microseconds, n as f64, "torn sample");
                checked += 1;
            }
        }

        stop.store(true, Ordering::Relaxed);
        let produced = producer.join().expect("producer panicked");

        assert!(
            checked > 0,
            "the consumer saw nothing, so this proved nothing"
        );
        assert!(produced > 0);
    }
}
