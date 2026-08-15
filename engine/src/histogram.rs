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
    /// u64 rather than u32: at the 400,000 samples per second measured on this rig a u32 bucket
    /// saturates in about three hours, which panics in debug and wraps in release - either way
    /// corrupting the distribution during exactly the long soak a marginal cable is found in.
    buckets: Vec<u64>,
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

    /// Samples beyond the linear range, where only a lower bound is known.
    pub fn overflow(&self) -> u64 {
        self.overflow
    }

    /// The value below which `fraction` of samples fall, in microseconds.
    ///
    /// Returns the bucket's upper edge, so the reported figure is never optimistic - a p99 of 120
    /// means 99% were under 120 µs, not "about 120".
    ///
    /// Beyond the linear range only a lower bound exists, and this returns that bound
    /// (`LINEAR_BUCKETS`) rather than the largest sample ever seen. Returning the maximum was a
    /// real defect: with a thousand samples at 50 ms and one at 356 ms, every fraction from p50 to
    /// p99.9 reported 356 ms - a sevenfold overstatement of the median, and identical p50 and p99,
    /// which is the signature of a distribution that has stopped being measured. A caller wanting
    /// the extreme has [`max_micros`](Self::max_micros); a caller wanting to know the bound was hit
    /// has [`overflow`](Self::overflow).
    pub fn percentile(&self, fraction: f64) -> f64 {
        if self.count == 0 {
            return 0.0;
        }

        let target = (fraction.clamp(0.0, 1.0) * self.count as f64).ceil() as u64;
        let mut seen = 0u64;

        for (micros, &n) in self.buckets.iter().enumerate() {
            seen += n;
            if seen >= target {
                return micros as f64 + 1.0;
            }
        }

        LINEAR_BUCKETS as f64
    }

    /// Folds another histogram's samples into this one.
    ///
    /// The capture thread accumulates locally and merges on a timer; the sampler takes a window
    /// and clears. Merging rather than assigning is what keeps those two from fighting over the
    /// same counts - an assignment would resurrect samples the sampler had already reported.
    pub fn merge(&mut self, other: &Self) {
        for (mine, theirs) in self.buckets.iter_mut().zip(other.buckets.iter()) {
            *mine += *theirs;
        }

        self.overflow += other.overflow;
        self.count += other.count;
        if other.max_micros > self.max_micros {
            self.max_micros = other.max_micros;
        }
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

    /// Regression: a single extreme sample used to become the answer for every fraction, because
    /// the overflow path returned the largest value seen. One 356 ms outlier among a thousand
    /// 50 ms samples reported p50 = p99 = p99.9 = 356 ms - a sevenfold overstatement of the median
    /// by one frame, and the identical-p50-and-p99 signature of a measurement that has stopped
    /// measuring.
    #[test]
    fn one_outlier_does_not_become_every_percentile() {
        let mut h = LatencyHistogram::new();
        for _ in 0..1000 {
            h.record(50_000.0);
        }
        h.record(356_502.0);

        let p50 = h.percentile(0.50);
        let p99 = h.percentile(0.99);

        assert!(
            p50 <= LINEAR_BUCKETS as f64,
            "p50 must not report the extreme: {p50}"
        );
        assert_eq!(
            p50, p99,
            "both are beyond the range, so both report the same bound"
        );
        assert_eq!(
            p50, LINEAR_BUCKETS as f64,
            "the honest answer is the bound, not the maximum"
        );

        // The extreme is still available, just not disguised as a percentile.
        assert_eq!(h.max_micros(), 356_502.0);
        assert_eq!(h.overflow(), 1001);
    }

    #[test]
    fn beyond_the_linear_range_reports_the_bound_and_keeps_the_maximum() {
        let mut h = LatencyHistogram::new();
        h.record(50_000.0);

        assert_eq!(h.percentile(0.99), LINEAR_BUCKETS as f64);
        assert_eq!(h.max_micros(), 50_000.0);
        assert_eq!(h.overflow(), 1);
    }

    /// A receive timestamp earlier than its send timestamp is impossible on one clock, so it is a
    /// corrupt frame rather than a zero-latency one, and must not drag the distribution down.
    #[test]
    fn impossible_latencies_are_discarded() {
        let mut h = LatencyHistogram::new();
        h.record(-1.0);
        h.record(f64::NAN);
        h.record(f64::INFINITY);

        assert_eq!(h.count(), 0);
    }

    /// Regression: reset had no callers, so published percentiles were lifetime aggregates rather
    /// than the sampling window. A one-second fault raised p99 and ten minutes of healthy traffic
    /// then erased it - the opposite of the transient-preserving behaviour the engine claims.
    #[test]
    fn merge_folds_counts_and_keeps_the_larger_maximum() {
        let mut a = LatencyHistogram::new();
        a.record(10.0);
        a.record(20_000.0);

        let mut b = LatencyHistogram::new();
        b.record(30.0);
        b.record(50_000.0);

        a.merge(&b);

        assert_eq!(a.count(), 4);
        assert_eq!(a.overflow(), 2);
        assert_eq!(a.max_micros(), 50_000.0);
        assert_eq!(a.percentile(0.25), 11.0, "the merged low bucket survives");
    }

    #[test]
    fn reset_clears_every_field() {
        let mut h = LatencyHistogram::new();
        h.record(42.0);
        h.record(500_000.0);

        h.reset();

        assert_eq!(h.count(), 0);
        assert_eq!(h.overflow(), 0);
        assert_eq!(h.max_micros(), 0.0);
        assert_eq!(h.percentile(0.99), 0.0);
    }
}
