//! Runs the reserved-multicast sweep and reports which addresses crossed.
//!
//! Phase 4 asks whether two NICs are wired to each other or have a switch between them. This is the
//! wire half of the answer: send a frame to each of a few link-local group addresses that a
//! conforming 802.1 relay must not forward, plus one address nothing may filter, and see what
//! arrives at the far adapter.
//!
//! The interesting result is not the verdict but the *pattern*. All of them crossing looks like a
//! cable; everything except the Slow Protocols address crossing is the Realtek default strap;
//! nothing crossing is a conforming bridge. That fingerprint is worth more in a report than a bare
//! direct/switch answer, and it costs nothing extra to collect.
//!
//! Every frame goes out on the engine's own EtherType, `0x88B5` - RFC 5342's local experimental
//! type - and never a protocol's own. A frame to the Slow Protocols address carrying `0x8809` is
//! an LACP frame to anything listening, and this tool has no business injecting those.

use std::env;
use std::time::{Duration, Instant};

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::topology::{self, SWEEP};
use ethlink_engine::{frame, PROBE_ETHERTYPE};

/// Minimum Ethernet frame, less the FCS the NIC appends.
const FRAME_LEN: usize = frame::MIN_BUFFER;

/// Frames per address. A handful, because one lost frame must not read as a filtered one.
const REPEATS: u32 = 20;

/// How long to wait for the sweep to arrive once it has all been sent.
const SETTLE: Duration = Duration::from_millis(500);

/// Distinct per invocation, so two overlapping runs cannot read each other's frames as their own.
fn run_id() -> u16 {
    std::process::id() as u16
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 5 {
        eprintln!("usage: topocheck <tx-guid> <rx-guid> <tx-mac> <rx-mac>");
        std::process::exit(2);
    }
    require_npcap();

    let tx_mac = mac(&args[3]).expect("bad TX MAC");
    let _rx_mac = mac(&args[4]).expect("bad RX MAC");

    let tx_name = device(&args[1]).expect("no device matching the TX GUID");
    let rx_name = device(&args[2]).expect("no device matching the RX GUID");
    println!("  TX {tx_name}");
    println!("  RX {rx_name}");

    // Receiver first, so it is listening before anything is sent. Promiscuous is not optional:
    // none of these group addresses is in the adapter's multicast list, so the NIC would drop
    // every one of them at the hardware filter.
    let mut rx = pcap::Capture::from_device(rx_name.as_str())
        .expect("open rx")
        .promisc(true)
        .immediate_mode(true)
        .timeout(200)
        .open()
        .expect("activate rx");

    let run_id = run_id();
    rx.filter(&frame::filter(run_id), true).expect("set filter");
    println!("  filter {}", frame::filter(run_id));

    let mut tx = pcap::Capture::from_device(tx_name.as_str())
        .expect("open tx")
        .open()
        .expect("activate tx");

    println!(
        "\nsending {REPEATS} frames to each of {} addresses",
        SWEEP.len()
    );
    let mut seq = 0u32;
    for probe in SWEEP {
        // Built per address, because the destination is the whole experiment. No VLAN tag: Npcap
        // strips one and re-applies it as NDIS out-of-band metadata, so a tagged probe does not
        // reach the wire as written.
        let mut buffer = frame::build(probe.mac, tx_mac, FRAME_LEN, run_id);
        for _ in 0..REPEATS {
            frame::stamp(&mut buffer, seq, frame::UNTIMED);
            seq = seq.wrapping_add(1);
            if tx.sendpacket(buffer.as_slice()).is_err() {
                eprintln!("  send failed for {}", probe.name);
                break;
            }
        }
        println!("  sent {:<22} {}", probe.name, format_mac(&probe.mac));
    }

    let mut arrived = [0u32; SWEEP.len()];
    let deadline = Instant::now() + SETTLE;
    while Instant::now() < deadline {
        match rx.next_packet() {
            Ok(packet) => {
                if frame::parse(packet.data, run_id).is_none() {
                    continue;
                }
                if let Some(probe) = topology::addressed_to(packet.data) {
                    if let Some(index) = SWEEP.iter().position(|p| p.name == probe.name) {
                        arrived[index] += 1;
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

    println!("\n{:<22} {:>8} {:>10}", "address", "arrived", "of");
    for (index, probe) in SWEEP.iter().enumerate() {
        println!(
            "{:<22} {:>8} {:>10}{}",
            probe.name,
            arrived[index],
            REPEATS,
            if probe.discriminating {
                "   <- decides"
            } else {
                ""
            }
        );
    }

    let control = arrived[0];
    let discriminator = SWEEP
        .iter()
        .position(|p| p.discriminating)
        .map(|i| arrived[i])
        .unwrap_or(0);

    // The control first, always. Nothing else in the sweep means anything until the path is known
    // to be carrying frames at all: a bridge filtering the probe and a capture that never
    // delivered it produce exactly the same silence.
    println!(
        "\nVERDICT  : {}",
        if control == 0 {
            "INCONCLUSIVE - the control frame never arrived, so this measures nothing.\n           \
             Check that the link is up, that the adapters are the right way round, and that\n           \
             promiscuous mode was accepted by the miniport."
                .to_owned()
        } else if discriminator == 0 {
            "SOMETHING IS BRIDGING - the control crossed and 01:80:C2:00:00:02 did not,\n           \
             so a relay component in the path is applying 802.1 filtering rules."
                .to_owned()
        } else {
            "NO 802.1 RELAY IN THE PATH - 01:80:C2:00:00:02 crossed, which no conforming\n           \
             bridge would allow. A media converter or a PHY repeater would also pass it,\n           \
             so this is strong evidence of a direct cable rather than proof of one."
                .to_owned()
        }
    );

    println!(
        "\nEtherType 0x{PROBE_ETHERTYPE:04X} throughout, never a protocol's own, so nothing on the\n\
         network mistakes these for real LACP, OAM or LLDP frames."
    );

    std::process::exit(i32::from(control == 0));
}

fn format_mac(mac: &[u8; 6]) -> String {
    mac.iter()
        .map(|b| format!("{b:02X}"))
        .collect::<Vec<_>>()
        .join(":")
}
