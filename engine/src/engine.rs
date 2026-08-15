//! Runs traffic across the link and turns it into telemetry.
//!
//! Three threads. One transmits in batches, one captures and times, one samples the shared
//! counters at a fixed cadence and publishes into the ring. Splitting the sampler out matters:
//! rates have to be computed over a known interval, and doing that on the transmit thread would
//! make the measurement depend on how busy the measurement is.
//!
//! # What the latency figure includes, and what it does not
//!
//! Timing one frame per batch (see [`frame::stamp`]) removes this engine's own queue from the
//! measurement. It does not remove the *driver's*. `pcap_sendqueue_transmit` returns when the
//! driver has accepted the batch, not when the wire has carried it, so the next batch's first
//! frame can still be sitting behind the previous one inside the NIC.
//!
//! Measured on the reference rig: 1518-byte frames report p50 480 µs and p99 700 µs at 932 Mbps,
//! where the driver's byte-limited buffer holds roughly one batch. 64-byte frames report p50
//! 5,500 µs at 150 Mbps, because the same buffer holds far more small frames. The 64-byte figure
//! is therefore a property of the transmitting NIC rather than of the cable, which is the same
//! conclusion `bin/txbench.rs` reached about small-frame throughput on this hardware.
//!
//! Removing that last term needs either paced transmission - offering frames at the rate the link
//! sustains, so the driver's buffer never runs deep - or NIC hardware timestamping, which the plan
//! records as the real answer and which neither adapter here provides.

use std::collections::VecDeque;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicU16, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use pcap::sendqueue::{SendQueue, SendSync};

use crate::frame;
use crate::histogram::LatencyHistogram;
use crate::ring::TelemetryRing;
use crate::{TelemetrySample, WIRE_OVERHEAD_BYTES};

/// Telemetry cadence. 60 Hz matches the chart, and sampling this often is what lets a transient
/// survive to be seen rather than being averaged flat.
const SAMPLE_INTERVAL: Duration = Duration::from_micros(16_667);

/// How many samples a published figure looks back over: 30 at 60 Hz, so half a second.
///
/// Throughput must not be computed over one 16.7 ms window. Frames are queued and transmitted in
/// batches, and the counters advance once per batch, so a window either contains a whole batch or
/// none of one - which made the reported rate bimodal and let it read 12 Gbps on a gigabit link.
/// Averaging over a span that reliably contains several batches removes the quantisation without
/// hiding anything real: a genuine half-second of over-line-rate throughput is a finding, and this
/// still shows it.
///
/// Latency uses the same span for a different reason. One frame per batch is timed - see
/// [`frame::stamp`] - so a 16.7 ms window holds one or two arrivals, and a p99 drawn from two
/// samples is just the larger of them. Percentiles are therefore accumulated across the window and
/// published when it closes, rather than smoothed: a moving average is exactly what destroys the
/// tail spike a marginal cable gives, and the tail is the whole reason percentiles are reported at
/// all.
const RATE_WINDOW_SAMPLES: usize = 30;

/// Wire time one transmit batch holds.
///
/// Batching is what makes line rate reachable at all - a frame per call manages about 6,800 frames
/// a second against the 1,488,095 that 64-byte gigabit needs - so this wants to be generous. What
/// bounds it is `stop()`: a transmit in flight cannot be interrupted, so this is also how long a
/// stop may take to be noticed.
///
/// It used to bound the latency measurement too, which is why it was 2 ms and why that was wrong.
/// A frame stamped on the way into the queue carries its own queue position, so the reported
/// latency described this buffer rather than the cable - and shrinking the buffer to fix that cost
/// 28% of throughput at 64-byte frames without ever fully working. Timing only the frame that
/// leaves first (see [`frame::stamp`]) separates the two concerns, and lets this be sized for
/// throughput alone.
///
/// 4 ms also sets the latency sampling rate, because exactly one frame per batch is timed: 250
/// batches a second is 125 timed arrivals per half-second window, which is enough to support a p99.
/// One per 8 ms batch was not - windows held one sample or none, and the published p50 and p99 were
/// identical, which the histogram's own documentation calls the signature of a measurement that has
/// stopped measuring.
const SEND_QUEUE_WIRE_TIME: Duration = Duration::from_millis(4);

/// Assumed link rate when the caller does not know one. Gigabit is the floor this rig runs at.
const DEFAULT_LINK_BITS_PER_SECOND: u64 = 1_000_000_000;

/// 100-nanosecond ticks from 0001-01-01 to the Unix epoch.
///
/// `TelemetrySample.TimestampTicks` is read on the managed side as `DateTimeOffset` ticks, and the
/// simulator writes exactly that. The engine wrote nanoseconds since its own start, so every native
/// sample plotted at a date near year 1 with intervals ten times too long - two engines behind one
/// interface, disagreeing about what the shared field means.
const TICKS_TO_UNIX_EPOCH: i64 = 621_355_968_000_000_000;

#[derive(Clone, Debug)]
pub struct RunConfig {
    pub tx_device: String,
    pub rx_device: String,
    pub tx_mac: [u8; 6],
    pub rx_mac: [u8; 6],
    /// Buffer length handed to pcap; the NIC appends the 4-byte FCS.
    pub frame_len: usize,
    /// The negotiated link rate. Sizes the transmit batch by wire time; 0 means "assume gigabit".
    pub link_bits_per_second: u64,
}

/// Why a run could not start.
///
/// Distinct from `pcap::Error` so the host can tell "you asked for something impossible" from
/// "the adapter would not open". Both used to surface as the same code, which made a mistyped
/// frame size look like a driver problem.
#[derive(Debug)]
pub enum StartError {
    /// Transmit and receive are the same adapter, so nothing would cross the cable.
    SameDevice,
    /// The frame length is outside what a legal Ethernet frame can carry.
    FrameLength(usize),
    Capture(pcap::Error),
}

impl From<pcap::Error> for StartError {
    fn from(error: pcap::Error) -> Self {
        Self::Capture(error)
    }
}

impl std::fmt::Display for StartError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::SameDevice => write!(
                f,
                "transmit and receive are the same adapter, so no frame would cross a cable"
            ),
            Self::FrameLength(len) => write!(
                f,
                "frame buffer of {len} bytes is outside {}..={}",
                frame::MIN_BUFFER,
                frame::MAX_BUFFER
            ),
            Self::Capture(error) => write!(f, "{error}"),
        }
    }
}

impl std::error::Error for StartError {}

/// Why a run stopped producing, if it has.
///
/// A worker that dies silently is the worst failure this engine can have. Measured before this
/// existed: the receive thread exited on a transient pcap error when an adapter was disabled, and
/// for the next 27 seconds - with the cable healthy and transmit running at a real 971 Mbps - the
/// app reported 100% packet loss, zero errors, a frozen latency figure, and a state of Running.
/// It called itself healthy while displaying a catastrophic result. This app exists to force
/// adapter settings mid-run, so capture errors are routine rather than exotic.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EngineFault {
    None = 0,
    ReceiveStopped = 1,
    TransmitStopped = 2,
    WorkerPanicked = 3,
    /// Transmit is reporting more traffic than the link can physically carry.
    ///
    /// Frames are counted when the driver accepts them, not when the wire has carried them, and a
    /// driver whose link has gone down accepts them as fast as memory allows and discards them.
    /// Measured while proving the receive fault above: the far adapter was disabled mid-run and
    /// transmit immediately reported 11,336 Mbps on a gigabit link, alongside a receive count that
    /// rendered as 92% packet loss. Both numbers were nonsense and neither looked it.
    ///
    /// Reported rather than clamped. Clamping to line rate would produce a plausible 1,000 Mbps
    /// reading for a cable that was carrying nothing at all, which is the failure this whole
    /// engine is built to avoid.
    TransmitExceedsLineRate = 4,
}

/// How far above line rate a reported figure may sit before it is treated as impossible.
///
/// Not zero, because transmit and receive counters are sampled at slightly different instants and
/// a full window can land a few percent either side. Ten percent is far below the sixfold
/// overshoot a dropped link produces and far above any sampling skew.
const LINE_RATE_TOLERANCE: f64 = 1.10;

#[derive(Default)]
struct Counters {
    tx_frames: AtomicU64,
    tx_bytes: AtomicU64,
    /// Frames the kernel filter matched. Authoritative even when userspace cannot keep up.
    rx_frames: AtomicU64,
    /// Frames the kernel matched but the capture buffer lost. Not cable loss - our own shortfall.
    rx_capture_drops: AtomicU64,
    /// Bytes on the wire for one frame, so receive throughput can be derived from the kernel's
    /// frame count rather than accumulated in the capture loop. Those are different populations -
    /// the capture loop sees only what reached userspace - and mixing them reported 0.44% loss by
    /// frame count and 69% loss by throughput from the same sample.
    frame_bytes: AtomicU64,
    /// Non-zero once something has gone wrong. Read by the host through `elt_engine_fault`.
    fault: AtomicU32,
}

impl Counters {
    /// Records a fault, keeping whichever arrived first.
    ///
    /// Later faults are usually consequences of the earlier one. Disabling the far adapter mid-run
    /// stops the capture and *then* makes transmit report 11 Gbps on a gigabit link; both are
    /// true, but "the capture stopped" is the cause and the one worth putting in front of someone
    /// holding a cable. Letting the last writer win made which of the two appeared arbitrary.
    fn record_fault(&self, fault: EngineFault) {
        let _ = self.fault.compare_exchange(
            EngineFault::None as u32,
            fault as u32,
            Ordering::AcqRel,
            Ordering::Relaxed,
        );
    }
}

pub struct Engine {
    running: Arc<AtomicBool>,
    ring: Arc<TelemetryRing>,
    counters: Arc<Counters>,
    threads: Vec<JoinHandle<()>>,
}

impl Engine {
    pub fn start(config: RunConfig) -> Result<Self, StartError> {
        if config.tx_device == config.rx_device {
            return Err(StartError::SameDevice);
        }
        if config.frame_len < frame::MIN_BUFFER || config.frame_len > frame::MAX_BUFFER {
            return Err(StartError::FrameLength(config.frame_len));
        }

        let run_id = next_run_id();
        // Read before the config is handed to the transmit thread.
        let link_bits_per_second = config.link_bits_per_second;
        let counters = Arc::new(Counters::default());
        // Every frame this engine sends is the same size, so receive throughput can be derived
        // exactly from the kernel's frame count. Wire bytes, not buffer bytes - see
        // WIRE_OVERHEAD_BYTES for why the two must not be mixed.
        counters.frame_bytes.store(
            (config.frame_len + WIRE_OVERHEAD_BYTES) as u64,
            Ordering::Relaxed,
        );

        let latency = Arc::new(Mutex::new(LatencyHistogram::new()));
        let epoch = Instant::now();

        // Opened here so a bad device fails start() rather than dying inside a worker, where the
        // caller would see only silence.
        let rx_capture = pcap::Capture::from_device(config.rx_device.as_str())?
            .promisc(true)
            .immediate_mode(true)
            .timeout(50)
            .open()?;
        let tx_capture = pcap::Capture::from_device(config.tx_device.as_str())?.open()?;

        // Built by pushing into a live Engine rather than into a vec. A panic part-way through
        // construction would drop the handles already created, leaving those threads running
        // against a `running` flag nobody would ever clear; dropping the Engine stops and joins
        // them instead.
        let mut engine = Self {
            running: Arc::new(AtomicBool::new(true)),
            ring: Arc::new(TelemetryRing::new()),
            counters,
            threads: Vec::with_capacity(3),
        };

        // Receive first, so the capture is listening before anything is transmitted.
        engine.threads.push(spawn_rx(
            rx_capture,
            run_id,
            Arc::clone(&engine.running),
            Arc::clone(&engine.counters),
            Arc::clone(&latency),
            epoch,
        )?);
        engine.threads.push(spawn_tx(
            tx_capture,
            config,
            run_id,
            Arc::clone(&engine.running),
            Arc::clone(&engine.counters),
            epoch,
        ));
        engine.threads.push(spawn_sampler(
            Arc::clone(&engine.running),
            Arc::clone(&engine.counters),
            latency,
            Arc::clone(&engine.ring),
            link_bits_per_second,
        ));

        Ok(engine)
    }

    pub fn ring(&self) -> &TelemetryRing {
        &self.ring
    }

    /// Why the run stopped producing, or `None`.
    ///
    /// Separate from the telemetry sample because that struct's layout is pinned byte-for-byte
    /// against the managed side; adding a field would silently break the agreement rather than
    /// fail to compile.
    pub fn fault(&self) -> EngineFault {
        match self.counters.fault.load(Ordering::Acquire) {
            1 => EngineFault::ReceiveStopped,
            2 => EngineFault::TransmitStopped,
            3 => EngineFault::WorkerPanicked,
            4 => EngineFault::TransmitExceedsLineRate,
            _ => EngineFault::None,
        }
    }

    pub fn stop(&mut self) {
        self.running.store(false, Ordering::Release);
        for handle in self.threads.drain(..) {
            let _ = handle.join();
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Identifies this run's frames on the wire.
///
/// Two engines on one rig - the obvious way to load a link in both directions at once - would
/// otherwise each count the other's frames as their own deliveries, and a cable dropping everything
/// in one direction would still report a full receive count. Sixteen bits is enough: the id only
/// has to distinguish runs that overlap in time on one wire.
fn next_run_id() -> u16 {
    static NEXT: AtomicU16 = AtomicU16::new(0);

    // Seeded from the clock so a restarted process does not reuse the previous process's ids while
    // its frames may still be sitting in a driver buffer.
    let seed = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|since| since.subsec_nanos() as u16)
        .unwrap_or(0);

    NEXT.fetch_add(1, Ordering::Relaxed)
        .wrapping_add(seed)
        .wrapping_add(std::process::id() as u16)
}

/// Bytes to allocate for the transmit batch: [`SEND_QUEUE_WIRE_TIME`] at the link's rate.
fn send_queue_bytes(link_bits_per_second: u64) -> u32 {
    let bits = if link_bits_per_second == 0 {
        DEFAULT_LINK_BITS_PER_SECOND
    } else {
        link_bits_per_second
    };

    let bytes = (bits / 8) as f64 * SEND_QUEUE_WIRE_TIME.as_secs_f64();

    // Floor: below about 64 KB the per-batch call starts to dominate at small frame sizes.
    // Ceiling: 8 MB is more than 10 Gbps needs and keeps a bad link-speed figure from asking for a
    // wild allocation.
    (bytes as u64).clamp(64 * 1024, 8 * 1024 * 1024) as u32
}

/// Runs a worker body, turning a panic into a reported fault.
///
/// A panicking thread otherwise unwinds into nothing: the run keeps its `Running` state, transmit
/// carries on, and the host displays a frozen count as though it were a measurement.
fn guard<F: FnOnce()>(counters: &Counters, body: F) {
    if catch_unwind(AssertUnwindSafe(body)).is_err() {
        counters.record_fault(EngineFault::WorkerPanicked);
    }
}

fn spawn_rx(
    mut capture: pcap::Capture<pcap::Active>,
    run_id: u16,
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    latency: Arc<Mutex<LatencyHistogram>>,
    epoch: Instant,
) -> Result<JoinHandle<()>, StartError> {
    // Filtering in the kernel is what makes the count trustworthy: the capture statistics then
    // describe this run's frames rather than everything on the wire, or another engine's.
    let filter = format!(
        "ether proto 0x{:04X} and ether[{}:2] = {}",
        crate::PROBE_ETHERTYPE,
        frame::RUN_ID_OFFSET,
        run_id
    );
    capture.filter(&filter, true)?;

    Ok(std::thread::spawn(move || {
        guard(&counters, || {
            let mut local = LatencyHistogram::new();
            let mut since_publish = Instant::now();

            while running.load(Ordering::Acquire) {
                match capture.next_packet() {
                    Ok(packet) => {
                        // Untimed frames still arrive and are still counted by the kernel filter;
                        // they simply carry no send time to subtract. Treating their zero as a
                        // timestamp would report a flood of zero-microsecond arrivals and drag
                        // every percentile to the floor.
                        if let Some((_seq, sent_nanos)) = frame::parse(packet.data, run_id)
                            .filter(|(_, sent)| *sent != frame::UNTIMED)
                        {
                            let now = epoch.elapsed().as_nanos() as u64;
                            // Not saturating_sub: a receive timestamp before its send timestamp is
                            // impossible on one clock, so clamping it to zero would launder a
                            // corrupt frame into a zero-latency one and drag the median down.
                            // Letting it go negative lets the histogram reject it, which is what
                            // that guard is for.
                            local.record((now as f64 - sent_nanos as f64) / 1_000.0);
                        }
                    }
                    // A timeout is the normal idle case. Everything else ends the capture, and the
                    // engine must say so rather than going quiet: a frozen receive count next to a
                    // healthy transmit reads as total packet loss, which is the most alarming thing
                    // this app can display and was, here, entirely false.
                    Err(pcap::Error::TimeoutExpired) => {}
                    Err(_) => {
                        counters.record_fault(EngineFault::ReceiveStopped);
                        break;
                    }
                }

                // Statistics and the histogram are published on a timer rather than per frame:
                // taking a lock at a million frames a second would cost more than the measurement.
                if since_publish.elapsed() >= SAMPLE_INTERVAL {
                    if let Ok(stats) = capture.stats() {
                        counters
                            .rx_frames
                            .store(u64::from(stats.received), Ordering::Relaxed);
                        counters
                            .rx_capture_drops
                            .store(u64::from(stats.dropped), Ordering::Relaxed);
                    }
                    // Merged rather than replaced, because the sampler clears the shared histogram
                    // when it takes a window and a wholesale copy would resurrect what it cleared.
                    if let Ok(mut shared) = latency.lock() {
                        shared.merge(&local);
                        local.reset();
                    }
                    since_publish = Instant::now();
                }
            }
        })
    }))
}

fn spawn_tx(
    mut capture: pcap::Capture<pcap::Active>,
    config: RunConfig,
    run_id: u16,
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    epoch: Instant,
) -> JoinHandle<()> {
    std::thread::spawn(move || {
        guard(&counters, || {
            let mut buffer = frame::build(config.rx_mac, config.tx_mac, config.frame_len, run_id);
            let wire_bytes = (buffer.len() + WIRE_OVERHEAD_BYTES) as u64;

            let queue_bytes = send_queue_bytes(config.link_bits_per_second);
            let mut queue = match SendQueue::new(queue_bytes) {
                Ok(queue) => queue,
                Err(_) => {
                    counters.record_fault(EngineFault::TransmitStopped);
                    return;
                }
            };

            let mut seq = 0u32;

            while running.load(Ordering::Acquire) {
                let mut queued = 0u64;
                loop {
                    // Only the first frame of the batch is timed. Every later one would carry the
                    // time it spent waiting for the frames ahead of it, which measures this queue
                    // rather than the link - and at 64 bytes that pushed both percentiles past the
                    // histogram's 10 ms ceiling while the cable was fine.
                    let sent_nanos = if queued == 0 {
                        epoch.elapsed().as_nanos() as u64
                    } else {
                        frame::UNTIMED
                    };

                    frame::stamp(&mut buffer, seq, sent_nanos);
                    if queue.queue(None, &buffer).is_err() {
                        break;
                    }
                    seq = seq.wrapping_add(1);
                    queued += 1;
                }

                if queued == 0 || queue.transmit(&mut capture, SendSync::Off).is_err() {
                    counters.record_fault(EngineFault::TransmitStopped);
                    break;
                }

                counters.tx_frames.fetch_add(queued, Ordering::Relaxed);
                counters
                    .tx_bytes
                    .fetch_add(queued * wire_bytes, Ordering::Relaxed);
            }
        })
    })
}

fn spawn_sampler(
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    latency: Arc<Mutex<LatencyHistogram>>,
    ring: Arc<TelemetryRing>,
    link_bits_per_second: u64,
) -> JoinHandle<()> {
    std::thread::spawn(move || {
        guard(&counters, || {
            let line_rate_megabits = link_bits_per_second as f64 / 1_000_000.0;
            // Seeded with the run's start so the first samples average over the time that has
            // actually elapsed rather than dividing by zero.
            let mut marks: VecDeque<(Instant, u64, u64)> =
                VecDeque::with_capacity(RATE_WINDOW_SAMPLES + 1);
            marks.push_back((Instant::now(), 0, 0));

            // Percentiles from the window that has closed, held steady until the next one does.
            let mut latency_window = 0usize;
            let mut published = (0.0, 0.0);

            while running.load(Ordering::Acquire) {
                std::thread::sleep(SAMPLE_INTERVAL);

                let now = Instant::now();
                let tx_bytes = counters.tx_bytes.load(Ordering::Relaxed);
                let rx_frames = counters.rx_frames.load(Ordering::Relaxed);
                let rx_bytes = rx_frames * counters.frame_bytes.load(Ordering::Relaxed);

                let (then, tx_then, rx_then) = *marks.front().expect("seeded above");
                let elapsed = now.duration_since(then).as_secs_f64();
                if elapsed <= 0.0 {
                    continue;
                }

                marks.push_back((now, tx_bytes, rx_bytes));
                if marks.len() > RATE_WINDOW_SAMPLES {
                    marks.pop_front();
                }

                // Taken and cleared when the window closes, so each figure describes a bounded span
                // of the run. Reading without ever clearing published a lifetime aggregate: a
                // one-second fault raised p99, and ten minutes of healthy traffic then erased it -
                // the opposite of letting a transient survive.
                latency_window += 1;
                if latency_window >= RATE_WINDOW_SAMPLES {
                    latency_window = 0;
                    if let Ok(mut histogram) = latency.lock() {
                        if histogram.count() > 0 {
                            published = (histogram.percentile(0.50), histogram.percentile(0.99));
                        }
                        histogram.reset();
                    }
                }
                let (p50, p99) = published;

                let tx_megabits = megabits(tx_bytes.saturating_sub(tx_then), elapsed);

                // Only once the window is full. A partial window divides a real byte delta by a
                // fraction of the interval, which can read high for entirely ordinary reasons.
                if marks.len() >= RATE_WINDOW_SAMPLES
                    && exceeds_line_rate(tx_megabits, line_rate_megabits)
                {
                    counters.record_fault(EngineFault::TransmitExceedsLineRate);
                }

                // The sampler thread is the ring's only producer for the engine's lifetime, which
                // is what push requires.
                ring.push(TelemetrySample {
                    timestamp_ticks: dotnet_ticks(),
                    tx_megabits_per_second: tx_megabits,
                    rx_megabits_per_second: megabits(rx_bytes.saturating_sub(rx_then), elapsed),
                    latency_p50_microseconds: p50,
                    latency_p99_microseconds: p99,
                    tx_frames: counters.tx_frames.load(Ordering::Relaxed) as i64,
                    rx_frames: rx_frames as i64,
                    rx_errors: counters.rx_capture_drops.load(Ordering::Relaxed) as i64,
                });
            }
        })
    })
}

fn megabits(bytes: u64, seconds: f64) -> f64 {
    bytes as f64 * 8.0 / 1_000_000.0 / seconds
}

/// The current time as .NET counts it: 100-nanosecond ticks since 0001-01-01 UTC.
///
/// Wall clock rather than the run's monotonic epoch, because the field is read as a date. That
/// makes the timestamp series vulnerable to an NTP correction stepping it backwards mid-run, which
/// a chart must tolerate rather than assume away - but a monotonic value in a field the host
/// formats as a date is wrong every time, not just occasionally.
fn dotnet_ticks() -> i64 {
    let since_epoch = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();

    TICKS_TO_UNIX_EPOCH + (since_epoch.as_nanos() / 100) as i64
}

/// Whether a reported rate is above what the link could carry.
///
/// A caller that did not supply a link rate gets no check, because there is nothing to compare
/// against - not because the reading is trusted.
fn exceeds_line_rate(megabits: f64, line_rate_megabits: f64) -> bool {
    line_rate_megabits > 0.0 && megabits > line_rate_megabits * LINE_RATE_TOLERANCE
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(tx: &str, rx: &str, frame_len: usize) -> RunConfig {
        RunConfig {
            tx_device: tx.to_owned(),
            rx_device: rx.to_owned(),
            tx_mac: [0; 6],
            rx_mac: [0; 6],
            frame_len,
            link_bits_per_second: 0,
        }
    }

    /// One adapter cannot test a cable, and pcap will happily open it twice - so the run would
    /// start, transmit, receive its own frames through the loopback the whole engine exists to
    /// avoid, and report a perfect link.
    #[test]
    fn one_adapter_for_both_directions_is_refused() {
        let error = Engine::start(config("\\Device\\NPF_{A}", "\\Device\\NPF_{A}", 1514));

        assert!(matches!(error, Err(StartError::SameDevice)));
    }

    /// Rejected at start rather than in the transmit loop, where an oversize frame fails every
    /// send and the run reads as a dead adapter.
    #[test]
    fn a_frame_no_link_can_carry_is_refused() {
        assert!(matches!(
            Engine::start(config("\\Device\\NPF_{A}", "\\Device\\NPF_{B}", 65_535)),
            Err(StartError::FrameLength(65_535))
        ));
        assert!(matches!(
            Engine::start(config("\\Device\\NPF_{A}", "\\Device\\NPF_{B}", 14)),
            Err(StartError::FrameLength(14))
        ));
    }

    /// The batch is sized in wire time so it scales with the link, and bounds how long a stop may
    /// take to be noticed - a transmit already in flight cannot be interrupted.
    fn drain_seconds(bits: u64) -> f64 {
        send_queue_bytes(bits) as f64 * 8.0 / bits as f64
    }

    #[test]
    fn the_transmit_batch_holds_the_intended_wire_time() {
        for bits in [1_000_000_000u64, 2_500_000_000, 5_000_000_000] {
            let seconds = drain_seconds(bits);

            assert!(
                (seconds - SEND_QUEUE_WIRE_TIME.as_secs_f64()).abs() < 0.000_5,
                "{bits} bps drains its batch in {seconds}s"
            );
        }
    }

    /// Outside the clamps the batch is bounded rather than exact, and both directions have to stay
    /// safe. A slow link gets a longer batch, which is how long a stop may take to be noticed. A
    /// fast one gets a shorter batch, which only means more batches a second - and so more timed
    /// frames, which is the direction that helps.
    #[test]
    fn the_clamps_keep_the_batch_within_safe_bounds() {
        let slow = drain_seconds(10_000_000);
        assert!(slow < 0.1, "10 Mbps: a stop would wait {slow}s");

        let fast = drain_seconds(100_000_000_000);
        assert!(fast < SEND_QUEUE_WIRE_TIME.as_secs_f64());
        assert!(fast > 0.000_5, "100 Gbps: {fast}s is too short to batch");
    }

    #[test]
    fn an_unknown_link_rate_falls_back_to_gigabit() {
        assert_eq!(
            send_queue_bytes(0),
            send_queue_bytes(DEFAULT_LINK_BITS_PER_SECOND)
        );
    }

    /// Overlapping runs must not share an id, or each counts the other's frames as delivered.
    #[test]
    fn run_ids_differ_between_runs() {
        assert_ne!(next_run_id(), next_run_id());
    }

    /// Measured, not hypothetical: disabling the far adapter mid-run made transmit report 11,336
    /// Mbps on a gigabit link, because the driver accepts frames at memory speed once the link is
    /// down and discards them. Nothing on screen said so.
    #[test]
    fn a_rate_above_line_rate_is_a_finding() {
        assert!(exceeds_line_rate(11_336.0, 1_000.0));
        assert!(exceeds_line_rate(1_500.0, 1_000.0));
    }

    /// Sampling skew puts a healthy full-rate link a few percent either side of line rate, and
    /// calling that a fault would fire on every good cable.
    #[test]
    fn a_healthy_link_at_full_rate_is_not_a_finding() {
        assert!(!exceeds_line_rate(947.0, 1_000.0));
        assert!(!exceeds_line_rate(1_050.0, 1_000.0));
    }

    /// An unknown link rate has nothing to compare against. Silence here is the absence of a
    /// check, not a verdict that the reading is sound.
    #[test]
    fn an_unknown_link_rate_disables_the_check() {
        assert!(!exceeds_line_rate(11_336.0, 0.0));
    }

    /// The timestamp is read on the managed side as a `DateTimeOffset`. A wrong epoch does not
    /// fail anything - it plots a chart dated year 1, or year 4000, with intervals off by a factor
    /// of ten, and every value on it is otherwise correct.
    #[test]
    fn the_timestamp_lands_in_this_century() {
        // 2020-01-01 and 2100-01-01 as .NET ticks.
        let ticks = dotnet_ticks();

        assert!(ticks > 637_134_336_000_000_000, "before 2020: {ticks}");
        assert!(ticks < 662_378_112_000_000_000, "after 2100: {ticks}");
    }

    /// One second of wall clock must be ten million ticks, or the chart's time axis is scaled.
    #[test]
    fn ticks_advance_ten_million_per_second() {
        let before = dotnet_ticks();
        std::thread::sleep(Duration::from_millis(50));
        let after = dotnet_ticks();

        let elapsed_seconds = (after - before) as f64 / 10_000_000.0;
        assert!(
            (0.04..0.30).contains(&elapsed_seconds),
            "a 50 ms sleep measured {elapsed_seconds}s"
        );
    }
}
