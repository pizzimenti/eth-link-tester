//! Runs traffic across the link and turns it into telemetry.
//!
//! Three threads. One transmits in batches, one captures and times, one samples the shared
//! counters at a fixed cadence and publishes into the ring. Splitting the sampler out matters:
//! rates have to be computed over a known interval, and doing that on the transmit thread would
//! make the measurement depend on how busy the measurement is.
//!
//! # What the latency figure includes, and what it does not
//!
//! Latency comes from one timed probe per batch, sent on its own immediately before the batch goes
//! out. Two sources of self-inflicted error were removed to get there. Timing a frame on its way
//! *into* the send queue made it report its own queue position rather than the link. Timing the
//! batch's first frame instead put the CPU cost of assembling the whole batch into the reading -
//! worth 1.3 ms at 64-byte frames, where a batch is some five thousand copies.
//!
//! What remains is the *driver's* buffer. `pcap_sendqueue_transmit` returns when the driver has
//! accepted the batch, not when the wire has carried it, so the next probe can still be sitting
//! behind the previous batch inside the NIC.
//!
//! Measured on the reference rig:
//!
//! | Frame | Throughput | p50 | p99 |
//! |---|---|---|---|
//! | 1518 B | 900 Mbps | 520 µs | 780 µs |
//! | 64 B | 188 Mbps | 3,700 µs | 5,900 µs |
//!
//! The 64-byte figure is a property of the transmitting NIC rather than of the cable - the same
//! conclusion `bin/txbench.rs` reached about small-frame throughput on this hardware - because the
//! driver's byte-limited buffer holds far more small frames than large ones.
//!
//! **The probe costs about 3.5% of throughput at 1518 bytes** (900 Mbps against 934 with the probe
//! inside the batch), because interleaving a single-frame send with a batched one leaves a bubble
//! in the driver's pipeline. That is the right way round to be wrong: throughput is understated by
//! a known, stated amount, where the latency it buys back was overstated by 26% at minimum frame
//! size with nothing on screen to say so. RFC 2544 measures throughput and latency in separate
//! tests for exactly this reason, and splitting them is what Phase 5 should do.
//!
//! Removing the last term needs either paced transmission - offering frames at the rate the link
//! sustains, so the driver's buffer never runs deep - or NIC hardware timestamping, which the plan
//! records as the real answer and which neither adapter here provides.

use std::collections::VecDeque;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicU16, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
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

/// Published for a window in which no timed probe arrived.
///
/// Zero rather than a sentinel because zero already means "nothing measured yet" here - it is what
/// the first samples of every run carry, before a window has closed - so the host and the charts
/// need no new case. A latency of exactly zero is not a reading anything can produce.
const NO_LATENCY: (f64, f64) = (0.0, 0.0);

#[derive(Default)]
struct Counters {
    tx_frames: AtomicU64,
    tx_bytes: AtomicU64,
    /// This run's frames that reached the receiving adapter: handed to the capture loop, plus the
    /// ones the kernel matched and the buffer then lost.
    ///
    /// **Not `ps_recv`, which is what this used to be, and which does not mean what the name
    /// suggests on this platform.** libpcap's `pcap_stats` documents `ps_recv` as platform-defined;
    /// the Npcap maintainers' definitive answer is that it counts "all packets on the interface
    /// that the Npcap driver has seen while this handle was open" - before the filter, everything on
    /// the wire. The filter-matched-and-delivered counter is `ps_capt`, which lives only in
    /// `pcap_stats_ex` and is not bound by the pcap crate at any version through 2.5.0.
    ///
    /// So the run-id filter protected the latency path, where `parse` gates every sample, and not
    /// the delivery count - the number this tool exists to publish. Every frame arriving at the RX
    /// adapter was credited as one of this run's, at this run's frame size: Windows chatter from
    /// both stacks, an STP hello every two seconds in exactly the switched topologies Phase 4 goes
    /// looking for, and worst of all a second overlapping run's traffic, which is the scenario the
    /// run id exists to prevent and which this counter reintroduced underneath it. The error was
    /// always flattering - delivery overstated, loss understated, receive throughput inflated.
    ///
    /// Both halves of the replacement are well-defined on Npcap. Frames delivered to the loop are
    /// counted where they arrive, after the kernel filter. `ps_drop` really is filter-scoped, so
    /// adding it keeps the property the old comment claimed: a frame the kernel matched still
    /// counts as delivered to the adapter even when userspace could not keep up with it, because
    /// reaching the NIC is what "received" means here and losing it afterwards is our shortfall,
    /// not the cable's.
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
    /// Cleared on its own by [`Engine::stop_transmit`], and by `stop` along with `running`.
    ///
    /// Transmit needs a flag of its own because the last frames sent are still in flight when
    /// transmit ends: the driver's send queue holds up to `SEND_QUEUE_WIRE_TIME` of traffic, and
    /// the receive thread only folds its kernel counts into the shared totals once per sample.
    /// Counting delivery at that instant charges every frame in either gap to loss. One flag for
    /// all three threads made that unavoidable - the only way to stop sending was to stop
    /// receiving in the same breath.
    transmitting: Arc<AtomicBool>,
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
            transmitting: Arc::new(AtomicBool::new(true)),
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
            Arc::clone(&engine.transmitting),
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

    /// Stops sending while receive and the sampler keep running.
    ///
    /// Delivery counted the instant transmit ends is always short, and by a fixed amount rather
    /// than a random one: frames sit in the driver's send queue for up to `SEND_QUEUE_WIRE_TIME`
    /// after the last `sendpacket` returns, and the receive thread folds its kernel counts into
    /// the shared totals only once per `SAMPLE_INTERVAL`. Both gaps count a frame as sent and not
    /// as received, which is indistinguishable from loss and biased in one direction, so it does
    /// not average out over a longer run - it shrinks, which is worse, because the same rig then
    /// reports a different loss figure for the same cable depending on how long it was measured.
    ///
    /// Call this, wait for the wire and the sampler to settle, then read the totals. It is also
    /// what RFC 2544 prescribes: stop the stream, wait, then count what arrived.
    pub fn stop_transmit(&self) {
        self.transmitting.store(false, Ordering::Release);
    }

    pub fn stop(&mut self) {
        self.transmitting.store(false, Ordering::Release);
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
    static SEED: OnceLock<u16> = OnceLock::new();

    // Seeded from the clock so a restarted process does not reuse the previous process's ids while
    // its frames may still be sitting in a driver buffer.
    //
    // Read once. Reading it per call made the id `counter + seed(call) + pid`, so two consecutive
    // ids collided whenever the second clock read happened to be one lower than the first - about
    // one adjacent pair in 65,536, since the nanosecond field is effectively uniform. That is
    // exactly the condition the run id exists to prevent: two overlapping runs sharing an id count
    // each other's frames as their own deliveries.
    let seed = *SEED.get_or_init(|| {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|since| since.subsec_nanos() as u16)
            .unwrap_or(0)
    });

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
    capture.filter(&frame::filter(run_id), true)?;

    Ok(std::thread::spawn(move || {
        guard(&counters, || {
            let mut local = LatencyHistogram::new();
            let mut since_publish = Instant::now();
            let mut last_dropped = 0u32;
            // Counted here rather than read back from pcap, and folded into the shared total on the
            // publish tick. Every packet reaching this arm has already passed the kernel's run-id
            // filter, so this is the kernel's own judgement of what belongs to this run - which is
            // what `ps_recv` was wrongly assumed to report. Local because a relaxed atomic add at a
            // million frames a second is a cost the measurement does not need to carry.
            let mut delivered = 0u64;

            while running.load(Ordering::Acquire) {
                match capture.next_packet() {
                    Ok(packet) => {
                        delivered += 1;

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
                    fold_receive(&mut capture, &counters, &mut delivered, &mut last_dropped);

                    // Merged rather than replaced, because the sampler clears the shared histogram
                    // when it takes a window and a wholesale copy would resurrect what it cleared.
                    if let Ok(mut shared) = latency.lock() {
                        shared.merge(&local);
                        local.reset();
                    }
                    since_publish = Instant::now();
                }
            }

            // Once more on the way out. The loop exits on a stop or a capture error, either of
            // which can land mid-window, and the frames counted since the last tick are as real as
            // any others - dropping them would charge a partial window to loss, which is the same
            // shape of error `stop_transmit` exists to prevent at the other end of the run.
            fold_receive(&mut capture, &counters, &mut delivered, &mut last_dropped);
        })
    }))
}

/// Folds one window's receive counts into the shared totals.
///
/// The drop delta is accumulated as a wrapping 32-bit difference rather than widened as though each
/// snapshot were a 64-bit lifetime total. pcap's counters are 32 bits and wrap: at 10 Gb/s with
/// 64-byte frames that is roughly every five minutes, and storing the raw snapshot would drop the
/// count by 4.3 billion at each wrap - throughput reading zero for a window and delivery ratios
/// corrupted for the rest of the run. A soak is exactly when this bites.
fn fold_receive(
    capture: &mut pcap::Capture<pcap::Active>,
    counters: &Counters,
    delivered: &mut u64,
    last_dropped: &mut u32,
) {
    let mut received = std::mem::take(delivered);

    if let Ok(stats) = capture.stats() {
        let dropped = stats.dropped.wrapping_sub(*last_dropped);
        *last_dropped = stats.dropped;

        counters
            .rx_capture_drops
            .fetch_add(u64::from(dropped), Ordering::Relaxed);

        // A frame the kernel matched and the buffer then lost still reached the adapter, so it is
        // a delivery. Counting it as loss would blame the cable for our own backlog.
        received += u64::from(dropped);
    }

    counters.rx_frames.fetch_add(received, Ordering::Relaxed);
}

fn spawn_tx(
    mut capture: pcap::Capture<pcap::Active>,
    config: RunConfig,
    run_id: u16,
    transmitting: Arc<AtomicBool>,
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

            while transmitting.load(Ordering::Acquire) {
                // The bulk of a batch carries no timestamp at all. A frame stamped on its way into
                // the queue would report the time it spent waiting for the frames ahead of it,
                // which measures this queue rather than the link - at 64 bytes that pushed both
                // percentiles past the histogram's 10 ms ceiling with a healthy cable.
                //
                // This stamps once and the refill loop below queues the same bytes repeatedly, so
                // **every bulk frame in a batch carries this one sequence number** while `seq`
                // still advances per frame. Nothing reads the sequence field today - spawn_rx
                // discards it - so it costs nothing now. It is recorded because the obvious next
                // use is reorder or loss detection keyed on that field, which would see thousands
                // of duplicates per batch and conclude the link was broken. Re-stamping per frame
                // is the wrong fix: paying that CPU cost inside the refill loop is exactly what
                // this design exists to avoid. A per-batch id plus a queue index is the shape that
                // works.
                frame::stamp(&mut buffer, seq, frame::UNTIMED);

                let mut queued = 0u64;
                loop {
                    if queue.queue(None, &buffer).is_err() {
                        break;
                    }
                    seq = seq.wrapping_add(1);
                    queued += 1;
                }

                if queued == 0 {
                    counters.record_fault(EngineFault::TransmitStopped);
                    break;
                }

                // One timed probe per batch, stamped and sent *after* the batch is assembled and
                // immediately before it goes out. Stamping the batch's first frame instead put the
                // CPU time spent building the whole batch into the latency figure - thousands of
                // copies at minimum frame size, and more at every faster link - so the central
                // measurement of this engine was systematically inflated by its own bookkeeping.
                //
                // Sent on its own rather than queued, because a queued probe cannot be re-stamped
                // once pcap has copied it. This is also the shape RFC 2544 prescribes: latency
                // belongs to a separate low-rate stream, not to the frames saturating the link.
                frame::stamp(&mut buffer, seq, epoch.elapsed().as_nanos() as u64);
                let probe = capture.sendpacket(buffer.as_slice()).is_ok();
                if probe {
                    seq = seq.wrapping_add(1);
                }

                if queue.transmit(&mut capture, SendSync::Off).is_err() {
                    counters.record_fault(EngineFault::TransmitStopped);
                    break;
                }

                let sent = queued + u64::from(probe);
                counters.tx_frames.fetch_add(sent, Ordering::Relaxed);
                counters
                    .tx_bytes
                    .fetch_add(sent * wire_bytes, Ordering::Relaxed);
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
            let mut published = NO_LATENCY;

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
                        // An empty window publishes nothing, rather than the window before it.
                        // Keeping the old pair meant a run that stopped producing timed probes
                        // went on reporting a plausible latency indefinitely - a figure nothing
                        // measured, carried forward at 60 Hz and indistinguishable from a live
                        // one. It is exactly reachable: one timed probe rides each batch, so at
                        // minimum frame size only about twenty land in a window, and losing them
                        // while bulk traffic still arrives is the case worth seeing rather than
                        // the case worth hiding.
                        published = if histogram.count() > 0 {
                            (histogram.percentile(0.50), histogram.percentile(0.99))
                        } else {
                            NO_LATENCY
                        };
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
