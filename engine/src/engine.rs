//! Runs traffic across the link and turns it into telemetry.
//!
//! Three threads. One transmits in batches, one captures and times, one samples the shared
//! counters at a fixed cadence and publishes into the ring. Splitting the sampler out matters:
//! rates have to be computed over a known interval, and doing that on the transmit thread would
//! make the measurement depend on how busy the measurement is.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use pcap::sendqueue::{SendQueue, SendSync};

use crate::frame;
use crate::histogram::LatencyHistogram;
use crate::ring::TelemetryRing;
use crate::TelemetrySample;

/// Telemetry cadence. 60 Hz matches the chart, and sampling this often is what lets a transient
/// survive to be seen rather than being averaged flat.
const SAMPLE_INTERVAL: Duration = Duration::from_micros(16_667);

/// Deliberately small. A large queue maximises throughput and destroys the latency measurement:
/// a frame is stamped when it is queued, so with a 4 MB batch - about 2,700 frames at 1518 bytes -
/// the last frame waits some 35 ms for the batch to drain, and the reported latency describes this
/// buffer rather than the cable. It also makes the rate bimodal, because counters advance once per
/// batch while the sampler runs every 16.7 ms, so some windows see nothing and others see
/// everything.
///
/// 256 KB is roughly 2 ms of wire time at gigabit, which is below the sample interval and small
/// enough that queueing no longer dominates a microsecond-scale measurement.
const SEND_QUEUE_BYTES: u32 = 256 * 1024;

#[derive(Clone, Debug)]
pub struct RunConfig {
    pub tx_device: String,
    pub rx_device: String,
    pub tx_mac: [u8; 6],
    pub rx_mac: [u8; 6],
    /// Buffer length handed to pcap; the NIC appends the 4-byte FCS.
    pub frame_len: usize,
}

#[derive(Default)]
struct Counters {
    tx_frames: AtomicU64,
    tx_bytes: AtomicU64,
    /// Frames the kernel filter matched. Authoritative even when userspace cannot keep up.
    rx_frames: AtomicU64,
    rx_bytes: AtomicU64,
    /// Frames the kernel matched but the capture buffer lost. Not cable loss - our own shortfall,
    /// and reported separately so the two are never confused.
    rx_capture_drops: AtomicU64,
}

pub struct Engine {
    running: Arc<AtomicBool>,
    ring: Arc<TelemetryRing>,
    threads: Vec<JoinHandle<()>>,
}

impl Engine {
    pub fn start(config: RunConfig) -> Result<Self, pcap::Error> {
        let running = Arc::new(AtomicBool::new(true));
        let ring = Arc::new(TelemetryRing::new());
        let counters = Arc::new(Counters::default());
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

        // Receive first, so the capture is listening before anything is transmitted.
        let threads = vec![
            spawn_rx(
                rx_capture,
                Arc::clone(&running),
                Arc::clone(&counters),
                Arc::clone(&latency),
                epoch,
            )?,
            spawn_tx(
                tx_capture,
                config,
                Arc::clone(&running),
                Arc::clone(&counters),
                epoch,
            ),
            spawn_sampler(
                Arc::clone(&running),
                Arc::clone(&counters),
                Arc::clone(&latency),
                Arc::clone(&ring),
                epoch,
            ),
        ];

        Ok(Self { running, ring, threads })
    }

    pub fn ring(&self) -> &TelemetryRing {
        &self.ring
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

fn spawn_rx(
    mut capture: pcap::Capture<pcap::Active>,
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    latency: Arc<Mutex<LatencyHistogram>>,
    epoch: Instant,
) -> Result<JoinHandle<()>, pcap::Error> {
    // Filtering in the kernel is what makes the count trustworthy: the capture statistics then
    // describe our frames rather than everything on the wire.
    let filter = format!("ether proto 0x{:04X}", crate::PROBE_ETHERTYPE);
    capture.filter(&filter, true)?;

    Ok(std::thread::spawn(move || {
        let mut local = LatencyHistogram::new();
        let mut since_publish = Instant::now();

        while running.load(Ordering::Acquire) {
            match capture.next_packet() {
                Ok(packet) => {
                    if let Some((_seq, sent_nanos)) = frame::parse(packet.data) {
                        let now = epoch.elapsed().as_nanos() as u64;
                        local.record(now.saturating_sub(sent_nanos) as f64 / 1_000.0);
                        counters
                            .rx_bytes
                            .fetch_add(packet.data.len() as u64, Ordering::Relaxed);
                    }
                }
                Err(pcap::Error::TimeoutExpired) => {}
                Err(_) => break,
            }

            // Statistics and the histogram are published on a timer rather than per frame: taking
            // a lock at a million frames a second would cost more than the measurement.
            if since_publish.elapsed() >= SAMPLE_INTERVAL {
                if let Ok(stats) = capture.stats() {
                    counters
                        .rx_frames
                        .store(u64::from(stats.received), Ordering::Relaxed);
                    counters
                        .rx_capture_drops
                        .store(u64::from(stats.dropped), Ordering::Relaxed);
                }
                if let Ok(mut shared) = latency.lock() {
                    *shared = local.clone();
                }
                since_publish = Instant::now();
            }
        }
    }))
}

fn spawn_tx(
    mut capture: pcap::Capture<pcap::Active>,
    config: RunConfig,
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    epoch: Instant,
) -> JoinHandle<()> {
    std::thread::spawn(move || {
        let mut buffer = frame::build(config.rx_mac, config.tx_mac, config.frame_len);
        let mut queue = match SendQueue::new(SEND_QUEUE_BYTES) {
            Ok(queue) => queue,
            Err(_) => return,
        };
        let mut seq = 0u32;

        while running.load(Ordering::Acquire) {
            let mut queued = 0u64;
            loop {
                // Stamped per frame, immediately before queueing, so the batch's own transit time
                // appears in the latency figure. That is honest: it is time the frame really spent
                // waiting to reach the wire.
                frame::stamp(&mut buffer, seq, epoch.elapsed().as_nanos() as u64);
                if queue.queue(None, &buffer).is_err() {
                    break;
                }
                seq = seq.wrapping_add(1);
                queued += 1;
            }

            if queued == 0 || queue.transmit(&mut capture, SendSync::Off).is_err() {
                break;
            }

            counters.tx_frames.fetch_add(queued, Ordering::Relaxed);
            counters
                .tx_bytes
                .fetch_add(queued * buffer.len() as u64, Ordering::Relaxed);
        }
    })
}

fn spawn_sampler(
    running: Arc<AtomicBool>,
    counters: Arc<Counters>,
    latency: Arc<Mutex<LatencyHistogram>>,
    ring: Arc<TelemetryRing>,
    epoch: Instant,
) -> JoinHandle<()> {
    std::thread::spawn(move || {
        let mut last = Instant::now();
        let mut last_tx_bytes = 0u64;
        let mut last_rx_bytes = 0u64;

        while running.load(Ordering::Acquire) {
            std::thread::sleep(SAMPLE_INTERVAL);

            let elapsed = last.elapsed().as_secs_f64();
            if elapsed <= 0.0 {
                continue;
            }
            last = Instant::now();

            let tx_bytes = counters.tx_bytes.load(Ordering::Relaxed);
            let rx_bytes = counters.rx_bytes.load(Ordering::Relaxed);

            let (p50, p99) = match latency.lock() {
                Ok(histogram) => (histogram.percentile(0.50), histogram.percentile(0.99)),
                Err(_) => (0.0, 0.0),
            };

            ring.push(TelemetrySample {
                timestamp_ticks: epoch.elapsed().as_nanos() as i64,
                tx_megabits_per_second: megabits(tx_bytes.saturating_sub(last_tx_bytes), elapsed),
                rx_megabits_per_second: megabits(rx_bytes.saturating_sub(last_rx_bytes), elapsed),
                latency_p50_microseconds: p50,
                latency_p99_microseconds: p99,
                tx_frames: counters.tx_frames.load(Ordering::Relaxed) as i64,
                rx_frames: counters.rx_frames.load(Ordering::Relaxed) as i64,
                rx_errors: counters.rx_capture_drops.load(Ordering::Relaxed) as i64,
            });

            last_tx_bytes = tx_bytes;
            last_rx_bytes = rx_bytes;
        }
    })
}

fn megabits(bytes: u64, seconds: f64) -> f64 {
    bytes as f64 * 8.0 / 1_000_000.0 / seconds
}
