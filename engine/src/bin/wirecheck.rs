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
    //
    // Sequence numbers are tracked rather than counted, because a count cannot tell "every frame
    // arrived" from "one arrived twice and another never did". Both reach `count`, and the second
    // is a loss this tool would have reported as a perfect result. That is not hypothetical here:
    // RUN_ID is a constant, so two overlapping wirecheck runs read each other's frames as their
    // own, and the tool's whole purpose is to be trusted about exactly this.
    let mut seen = vec![false; count as usize];
    let mut unique = 0u32;
    let mut duplicates = 0u32;
    let mut out_of_range = 0u32;
    let mut first_seq = None;
    let deadline = Instant::now() + Duration::from_secs(3);
    while Instant::now() < deadline && unique < count {
        match rx.next_packet() {
            Ok(packet) => {
                if let Some((seq, _sent)) = frame::parse(packet.data, RUN_ID) {
                    first_seq.get_or_insert(seq);

                    match seen.get_mut(seq as usize) {
                        // A sequence this run never sent. Another run's frame, or a corrupted one
                        // that still parsed - either way it must not count towards delivery.
                        None => out_of_range += 1,
                        Some(true) => duplicates += 1,
                        Some(slot) => {
                            *slot = true;
                            unique += 1;
                        }
                    }
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
    println!("received : {unique} of {count} distinct sequences");
    println!("first seq: {first_seq:?}");
    if duplicates > 0 || out_of_range > 0 {
        println!("duplicates: {duplicates}   out of range: {out_of_range}");
    }

    // Every frame, not merely some, and each exactly once. The original threshold was
    // `received > 0`, which passes while 999 of 1000 are lost - and the number this tool exists to
    // support is 1000 for 1000.
    let all_arrived = unique == count;

    println!(
        "\nVERDICT  : {}",
        match (all_arrived, unique) {
            (true, _) => "EVERY FRAME ARRIVED".to_owned(),
            (false, 0) => "NOTHING ARRIVED - premise not proven".to_owned(),
            (false, _) => format!("FAILED - only {unique} of {count} distinct frames arrived"),
        }
    );

    // Deliberately not claiming the frames touched copper. This tool counts frames in userspace at
    // both ends, and userspace cannot tell a frame that crossed a cable from one a bridge handed
    // back: a loop forwards our frame with its destination MAC unchanged, so it is byte-identical
    // to the real thing, and a capture on the sending adapter sees that adapter's own outbound
    // copy regardless. `pcap_setdirection` would separate them and Npcap does not implement it
    // (verified on 1.88 - the handle is refused).
    //
    // The instrument that *can* answer it is the NIC's own counters, which is why the claim has
    // always been phrased in terms of them. tools\Verify-Wire.ps1 brackets this run with those
    // counters and checks the reverse direction; run it rather than this when the question is
    // whether the premise holds.
    println!(
        "\nThis counts frames in userspace only. For the hardware-counter confirmation, and the\n\
         reverse-traffic check that rules out a bridge, run tools\\Verify-Wire.ps1."
    );

    std::process::exit(i32::from(!all_arrived));
}
