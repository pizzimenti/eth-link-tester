//! Measures how fast frames can actually be put on the wire, per frame size.
//!
//! A naive send loop manages about 6,800 frames per second because each frame is its own call.
//! 1000BASE-T at the RFC 2544 minimum frame size needs 1,488,095 - so the question is not whether
//! batching helps but whether it closes a 200x gap, and where the real ceiling sits. Reports what
//! was measured rather than what line rate would be.
//!
//! Measured on the reference rig, 2026-08-15, three seconds per size:
//!
//! | frame | Killer -> Realtek | Realtek -> Killer |
//! |-------|-------------------|-------------------|
//! | 64B   | 105 kpps (7.1%)   | 436 kpps (29.3%)  |
//! | 512B  | 210 kpps (89.2%)  | 218 kpps (93.0%)  |
//! | 1518B |  76 kpps (94.0%)  |  77 kpps (94.6%)  |
//!
//! **Those are acceptance rates, and only the 512B and 1518B rows have since been confirmed to
//! reach the far end.** This tool counts what the driver took from it, which is the only thing a
//! transmitter can count and is not the same as a frame arriving - a driver with no link accepts
//! frames at memory speed, which is how this rig once reported 11,336 Mbps on a gigabit cable.
//! `tools/Measure-Link.ps1` brackets a run with both NICs' hardware counters and settles it, and
//! at 64 bytes Realtek -> Killer it confirms **210k frames/s delivered** against the 436k accepted
//! above. Half. The 64-byte rows here are a ceiling on what can be offered, not a measurement of
//! what crosses.
//!
//! Three conclusions worth keeping. Large frames reach ~94% of line rate in both directions, so
//! the cable and the batching are not the limit. Small frames do not, and the limit is the
//! transmitting adapter rather than Npcap or this code - the USB dongle, which ought to be the
//! weaker adapter, accepts four times as many 64-byte frames as the Killer and delivers roughly
//! twice as many. And the asymmetry means a 64-byte result from this rig characterises the
//! transmitting NIC, not the cable: the same cable measured the other way looks far better. Any
//! report quoting a small-frame figure has to say which direction produced it - and the Killer ->
//! Realtek direction does not reproduce at all, varying between 49k and 255k frames/s across ten
//! identical runs, which is why the README publishes no number for it.
//!
//! Flow control was tested as a suspect and cleared: disabling it on both adapters changed 64-byte
//! throughput not at all and made mid-sizes worse.

use std::time::Instant;

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::{frame, WIRE_OVERHEAD_BYTES};
use pcap::sendqueue::{SendQueue, SendSync};

/// Buffer sizes handed to pcap. The NIC appends the 4-byte FCS, so these are the RFC 2544 frame
/// sizes minus four: a "64 byte frame" is 60 bytes of buffer.
const BUFFER_SIZES: [usize; 6] = [60, 124, 252, 508, 1020, 1514];

/// Deliberately larger than the engine's batch. This measures the transmit ceiling, so it wants
/// every byte of batching available; the engine trades some of that away to keep its latency
/// measurement about the cable rather than about its own queue.
const QUEUE_BYTES: u32 = 8 * 1024 * 1024;

/// Fixed: this tool only transmits, so no receiver is filtering on the id.
const RUN_ID: u16 = 1;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 4 {
        eprintln!("usage: txbench <tx-guid> <tx-mac> <rx-mac> [seconds-per-size]");
        std::process::exit(2);
    }
    require_npcap();

    let seconds: f64 = args.get(4).and_then(|s| s.parse().ok()).unwrap_or(2.0);
    let src = mac(&args[2]).expect("bad source MAC");
    let dst = mac(&args[3]).expect("bad destination MAC");

    let name = device(&args[1]).expect("no matching device");

    let mut cap = pcap::Capture::from_device(name.as_str())
        .expect("open")
        .open()
        .expect("activate");

    // "wire pps" excludes refill, so it is what the engine could reach if refill were overlapped
    // with transmission - which is the obvious next optimisation if the gap is large.
    println!(
        "{:<7} {:>11} {:>11} {:>8} {:>7} {:>12} {:>8} {:>7}",
        "frame", "frames", "pps", "Mbps", "ofline", "wire-pps", "wireMbps", "refill%"
    );

    for buffer_len in BUFFER_SIZES {
        let wire_frame = buffer_len + 4; // the NIC adds FCS
        let wire_bits = ((buffer_len + WIRE_OVERHEAD_BYTES) * 8) as f64;
        let line_rate_pps = 1_000_000_000.0 / wire_bits;

        let mut buffer = frame::build(dst, src, buffer_len, RUN_ID);
        let mut queue = SendQueue::new(QUEUE_BYTES).expect("alloc sendqueue");

        let mut sent = 0u64;
        let mut seq = 0u32;
        let mut refill_secs = 0.0f64;
        let mut wire_secs = 0.0f64;
        let started = Instant::now();

        while started.elapsed().as_secs_f64() < seconds {
            // transmit() resets the queue, so each pass refills the same allocation. Refill and
            // transmit are timed apart because they are different bottlenecks: refill is CPU in
            // this process, transmit is the wire. Conflating them made a 128-byte frame appear to
            // beat a 64-byte one on packets per second, which is impossible on the wire and is the
            // signature of a measurement dominated by per-frame queueing cost.
            let refill_start = Instant::now();
            let mut n = 0u32;
            loop {
                frame::stamp(&mut buffer, seq, 0);
                if queue.queue(None, &buffer).is_err() {
                    break;
                }
                seq = seq.wrapping_add(1);
                n += 1;
            }

            refill_secs += refill_start.elapsed().as_secs_f64();

            if n == 0 {
                eprintln!("queue would not accept a {buffer_len} byte frame");
                break;
            }

            let wire_start = Instant::now();
            if queue.transmit(&mut cap, SendSync::Off).is_err() {
                eprintln!("transmit failed at {buffer_len} bytes");
                break;
            }
            wire_secs += wire_start.elapsed().as_secs_f64();
            sent += u64::from(n);
        }
        let elapsed = started.elapsed().as_secs_f64();

        let pps = sent as f64 / elapsed;
        let wire_pps = if wire_secs > 0.0 {
            sent as f64 / wire_secs
        } else {
            0.0
        };
        println!(
            "{:<7} {:>11} {:>11.0} {:>8.1} {:>6.1}% {:>12.0} {:>8.1} {:>6.1}%",
            format!("{wire_frame}B"),
            sent,
            pps,
            pps * wire_bits / 1_000_000.0,
            100.0 * pps / line_rate_pps,
            wire_pps,
            wire_pps * wire_bits / 1_000_000.0,
            100.0 * refill_secs / elapsed
        );
    }
}
