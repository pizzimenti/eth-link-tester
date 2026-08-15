//! Latency percentiles from a fixed-bucket histogram.
//!
//! A sorted buffer would give exact percentiles and is the wrong shape here: at line rate the
//! sample count per window is in the hundreds of thousands, and sorting that on the measurement
//! thread introduces the jitter the measurement is trying to observe. Buckets cost one increment
//! per sample, need no allocation once built, and bound the error to the bucket width.
//!
//! p99.9 is why this exists at all. A marginal cable shows up in the tail, and a mean hides it
//! completely - the plan's whole argument for reporting percentiles rather than an average.

/// One bucket per microsecond up to this, then the overflow bucket.
///
/// 10 ms is far beyond any healthy same-machine one-way latency, so anything landing in overflow
/// is a finding rather than a measurement to be precise about.
const LINEAR_BUCKETS: usize = 10_000;

#[derive(Clone)]
pub struct LatencyHistogram {
    buckets: Vec<u32>,
    overflow: u64,
    count: u64,
    max_micros: f64,
}

impl Default for LatencyHistogram {
    fn default() -> Self {
        Self::new()
    }
}

impl LatencyHistogram {
    pub fn new() -> Self {
        Self {
            buckets: vec![0; LINEAR_BUCKETS],
            overflow: 0,
            count: 0,
            max_micros: 0.0,
        }
    }

    pub fn record(&mut self, micros: f64) {
        // A receive timestamp preceding its send timestamp cannot happen on one machine with one
        // clock, so a negative is a corrupt frame rather than a fast one. NaN is excluded by
        // is_finite, which also rejects an infinity that would otherwise land in overflow and be
        // reported as a real, enormous latency.
        if !micros.is_finite() || micros < 0.0 {
            return;
        }

        self.count += 1;
        if micros > self.max_micros {
            self.max_micros = micros;
        }

        let bucket = micros as usize;
        if bucket < LINEAR_BUCKETS {
            self.buckets[bucket] += 1;
        } else {
            self.overflow += 1;
        }
    }

    pub fn count(&self) -> u64 {
        self.count
    }

    pub fn max_micros(&self) -> f64 {
        self.max_micros
    }

    /// The value below which `fraction` of samples fall, in microseconds.
    ///
    /// Returns the bucket's upper edge, so the reported figure is never optimistic - a p99 of 120
    /// means 99% were under 120 µs, not "about 120".
    pub fn percentile(&self, fraction: f64) -> f64 {
        if self.count == 0 {
            return 0.0;
        }

        let target = (fraction.clamp(0.0, 1.0) * self.count as f64).ceil() as u64;
        let mut seen = 0u64;

        for (micros, &n) in self.buckets.iter().enumerate() {
            seen += u64::from(n);
            if seen >= target {
                return micros as f64 + 1.0;
            }
        }

        // Everything else is in overflow, where the only honest answer is the largest seen.
        self.max_micros
    }

    pub fn reset(&mut self) {
        self.buckets.fill(0);
        self.overflow = 0;
        self.count = 0;
        self.max_micros = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percentiles_are_upper_edges() {
        let mut h = LatencyHistogram::new();
        for micros in 0..100 {
            h.record(f64::from(micros));
        }

        assert_eq!(h.count(), 100);
        // 50 of 100 samples are below 50 µs, so p50 is that bucket's upper edge.
        assert_eq!(h.percentile(0.50), 50.0);
        assert_eq!(h.percentile(0.99), 99.0);
    }

    #[test]
    fn a_tail_moves_p99_but_not_p50() {
        let mut h = LatencyHistogram::new();
        for _ in 0..990 {
            h.record(10.0);
        }
        for _ in 0..10 {
            h.record(5000.0);
        }

        assert_eq!(h.percentile(0.50), 11.0);
        assert!(h.percentile(0.999) > 1000.0, "the tail must survive");
    }

    #[test]
    fn beyond_the_linear_range_reports_the_largest_seen() {
        let mut h = LatencyHistogram::new();
        h.record(50_000.0);

        assert_eq!(h.percentile(0.99), 50_000.0);
        assert_eq!(h.max_micros(), 50_000.0);
    }

    /// A receive timestamp earlier than its send timestamp is impossible on one clock, so it is a
    /// corrupt frame rather than a zero-latency one, and must not drag the distribution down.
    #[test]
    fn impossible_latencies_are_discarded() {
        let mut h = LatencyHistogram::new();
        h.record(-1.0);
        h.record(f64::NAN);

        assert_eq!(h.count(), 0);
    }
}
