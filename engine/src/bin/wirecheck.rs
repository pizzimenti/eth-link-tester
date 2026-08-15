//! Phase 3's make-or-break check: does a frame injected through Npcap actually cross the copper?
//!
//! Two NICs in one machine cannot be tested with sockets - Windows' TCP/IP stack recognises both
//! addresses as local and short-circuits the traffic through loopback, so the packets never reach
//! a PHY. Npcap sits as an NDIS lightweight filter *below* the stack, where there is no routing
//! decision left to short-circuit. This proves that claim or kills the project's premise.
//!
//! It builds its frames with `frame::build` rather than its own copy of the layout. The copy was
//! the point at first - an independent witness is worth more than a self-consistent one - but once
//! the engine shipped, a second definition of the header is just a second thing to get wrong, and
//! this check's value is now as a regression test of the engine's own frame.

use std::env;
use std::time::{Duration, Instant};

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::{frame, PROBE_ETHERTYPE};

/// Minimum Ethernet frame, less the FCS the NIC appends.
const FRAME_LEN: usize = frame::MIN_BUFFER;

/// Fixed, because this tool sends one burst and exits - there is nothing for a run id to
/// disambiguate, and a constant keeps the printed filter reproducible.
const RUN_ID: u16 = 1;

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 5 {
        eprintln!("usage: wirecheck <tx-guid> <rx-guid> <tx-mac> <rx-mac> [count]");
        std::process::exit(2);
    }
    require_npcap();

    let tx_mac = mac(&args[3]).expect("bad TX MAC");
    let rx_mac = mac(&args[4]).expect("bad RX MAC");
    let count: u32 = args.get(5).and_then(|s| s.parse().ok()).unwrap_or(100);

    let tx_name = device(&args[1]).expect("no device matching the TX GUID");
    let rx_name = device(&args[2]).expect("no device matching the RX GUID");
    println!("  TX {tx_name}");
    println!("  RX {rx_name}");

    // Receiver first, so it is listening before anything is sent.
    let mut rx = pcap::Capture::from_device(rx_name.as_str())
        .expect("open rx")
        .promisc(true)
        .immediate_mode(true)
        .timeout(200)
        .open()
        .expect("activate rx");
    rx.filter(&format!("ether proto 0x{PROBE_ETHERTYPE:04X}"), true)
        .expect("set filter");

    let mut tx = pcap::Capture::from_device(tx_name.as_str())
        .expect("open tx")
        .open()
        .expect("activate tx");

    println!("\nsending {count} frames, ethertype 0x{PROBE_ETHERTYPE:04X}, {FRAME_LEN} bytes each");
    let mut buffer = frame::build(rx_mac, tx_mac, FRAME_LEN, RUN_ID);
    let started = Instant::now();
    for seq in 0..count {
        frame::stamp(&mut buffer, seq, 0);
        tx.sendpacket(buffer.as_slice()).expect("send failed");
    }
    let send_elapsed = started.elapsed();

    // Drain what arrived, bounded so a total failure does not hang.
    let mut received = 0u32;
    let mut first_seq = None;
    let deadline = Instant::now() + Duration::from_secs(3);
    while Instant::now() < deadline && received < count {
        match rx.next_packet() {
            Ok(packet) => {
                if let Some((seq, _sent)) = frame::parse(packet.data, RUN_ID) {
                    first_seq.get_or_insert(seq);
                    received += 1;
                }
            }
            Err(pcap::Error::TimeoutExpired) => continue,
            Err(e) => {
                eprintln!("capture error: {e}");
                break;
            }
        }
    }

    println!(
        "\nsent     : {count} in {:.1} ms",
        send_elapsed.as_secs_f64() * 1000.0
    );
    println!("received : {received}");
    println!("first seq: {first_seq:?}");
    println!(
        "\nVERDICT  : {}",
        if received > 0 {
            "FRAMES CROSSED THE WIRE"
        } else {
            "NOTHING ARRIVED - premise not proven"
        }
    );
}
