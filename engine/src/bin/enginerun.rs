//! Runs the engine end to end and prints what comes out of the telemetry ring.
//!
//! The unit tests cover the ring and the histogram in isolation; this is the only thing that
//! exercises three threads, a real NIC and a real cable together, which is where the interesting
//! failures live.

use std::time::{Duration, Instant};

use ethlink_engine::diag::{device, mac};
use ethlink_engine::{Engine, RunConfig, TelemetrySample};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 5 {
        eprintln!(
            "usage: enginerun <tx-guid> <rx-guid> <tx-mac> <rx-mac> [frame-len] [seconds] [mbps]"
        );
        std::process::exit(2);
    }

    let frame_len: usize = args.get(5).and_then(|s| s.parse().ok()).unwrap_or(1514);
    let seconds: u64 = args.get(6).and_then(|s| s.parse().ok()).unwrap_or(5);
    let megabits: u64 = args.get(7).and_then(|s| s.parse().ok()).unwrap_or(1000);

    let config = RunConfig {
        tx_device: device(&args[1]).expect("no device matching the TX GUID"),
        rx_device: device(&args[2]).expect("no device matching the RX GUID"),
        tx_mac: mac(&args[3]).expect("bad TX MAC"),
        rx_mac: mac(&args[4]).expect("bad RX MAC"),
        frame_len,
        link_bits_per_second: megabits * 1_000_000,
    };

    println!("frame {} bytes, running {seconds}s\n", frame_len + 4);
    let mut engine = Engine::start(config).expect("engine start");

    let mut buffer = [TelemetrySample::default(); 256];
    let mut total_dropped = 0u64;
    let mut samples_seen = 0usize;
    let mut last = TelemetrySample::default();

    println!(
        "{:>6} {:>10} {:>10} {:>9} {:>9} {:>12} {:>12} {:>9}",
        "t(s)", "tx Mbps", "rx Mbps", "p50 us", "p99 us", "tx frames", "rx frames", "capdrop"
    );

    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(seconds) {
        std::thread::sleep(Duration::from_millis(500));

        // Sound: this loop is the ring's only consumer.
        let drained = unsafe { engine.ring().drain(&mut buffer) };
        total_dropped += drained.dropped;
        samples_seen += drained.count;

        if drained.count > 0 {
            last = buffer[drained.count - 1];
            println!(
                "{:>6.1} {:>10.1} {:>10.1} {:>9.1} {:>9.1} {:>12} {:>12} {:>9}",
                last.timestamp_ticks as f64 / 1e9,
                last.tx_megabits_per_second,
                last.rx_megabits_per_second,
                last.latency_p50_microseconds,
                last.latency_p99_microseconds,
                last.tx_frames,
                last.rx_frames,
                last.rx_errors
            );
        }
    }

    let fault = engine.fault();
    engine.stop();

    let delivered = if last.tx_frames > 0 {
        100.0 * last.rx_frames as f64 / last.tx_frames as f64
    } else {
        0.0
    };

    println!("\nfault           : {fault:?}");
    println!("samples drained : {samples_seen}");
    println!("ring drops      : {total_dropped}  (telemetry lost to a slow consumer, not frames)");
    println!("tx frames       : {}", last.tx_frames);
    println!(
        "rx frames       : {}  ({delivered:.2}% of sent)",
        last.rx_frames
    );
    println!(
        "capture drops   : {}  (our buffer, not the cable)",
        last.rx_errors
    );
}
