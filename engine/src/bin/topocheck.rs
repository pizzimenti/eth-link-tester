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
//!
//! The measurement itself lives in [`ethlink_engine::sweep`], because the managed host needs the
//! same answer and a second implementation is a second thing that can disagree with the first.

use std::env;

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::sweep::{self, DEFAULT_REPEATS};
use ethlink_engine::topology::SWEEP;
use ethlink_engine::PROBE_ETHERTYPE;

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

    println!(
        "\nsending {DEFAULT_REPEATS} frames to each of {} addresses",
        SWEEP.len()
    );
    for probe in SWEEP {
        println!("  {:<22} {}", probe.name, format_mac(&probe.mac));
    }

    // Per-invocation rather than fixed. Frames from a previous sweep can still be in an NPF buffer,
    // and sharing an id would count them as this sweep's arrivals.
    let run_id = ethlink_engine::engine::next_run_id();

    let outcome = match sweep::run(&tx_name, &rx_name, tx_mac, DEFAULT_REPEATS, run_id) {
        Ok(outcome) => outcome,
        Err(e) => {
            eprintln!("\nsweep failed: {e}");
            std::process::exit(1);
        }
    };

    println!("\n{:<22} {:>8} {:>10}", "address", "arrived", "of");
    for (index, probe) in SWEEP.iter().enumerate() {
        println!(
            "{:<22} {:>8} {:>10}{}",
            probe.name,
            outcome.arrived[index],
            outcome.sent,
            if probe.discriminating {
                "   <- decides"
            } else {
                ""
            }
        );
    }

    let control = outcome.control();
    let discriminator = outcome.discriminator();

    // The control first, always. Nothing else in the sweep means anything until the path is known
    // to be carrying frames at all: a bridge filtering the probe and a capture that never
    // delivered it produce exactly the same silence.
    //
    // And then a second gate on how well it carried them. One control frame in twenty used to be
    // enough to call the path measuring, which on a link losing 95% of frames lets all twenty
    // discriminator frames vanish by chance about a third of the time - printing a confident
    // bridging verdict about a marginal cable.
    println!(
        "\nVERDICT  : {}",
        if control == 0 {
            "INCONCLUSIVE - the control frame never arrived, so this measures nothing.\n           \
             Check that the link is up, that the adapters are the right way round, that\n           \
             promiscuous mode was accepted by the miniport, and - if something is in the\n           \
             path - that it is not filtering unregistered multicast groups, which 802.1Q\n           \
             8.8.6 permits."
                .to_owned()
        } else if discriminator > 0 {
            "NO 802.1 RELAY IN THE PATH - 01:80:C2:00:00:02 crossed, which no conforming\n           \
             bridge would allow. A media converter or a PHY repeater would also pass it,\n           \
             so this corroborates a direct cable rather than proving one."
                .to_owned()
        } else if outcome.control_is_healthy() {
            "SOMETHING IS BRIDGING - the control crossed and 01:80:C2:00:00:02 did not,\n           \
             so a relay component in the path is applying 802.1 filtering rules."
                .to_owned()
        } else {
            format!(
                "INCONCLUSIVE - 01:80:C2:00:00:02 did not arrive, but neither did much\n           \
                 else: the control managed {control} of {}. At that delivery rate the\n           \
                 whole probe can go missing by chance, so this is not evidence of\n           \
                 filtering. The loss rate is the finding, and it is about the cable.",
                outcome.sent
            )
        }
    );

    println!(
        "\nEtherType 0x{PROBE_ETHERTYPE:04X} throughout, never a protocol's own, so nothing on the\n\
         network mistakes these for real LACP, OAM or LLDP frames."
    );

    // Non-zero for either inconclusive outcome, not just a dead control. A script that treats "the
    // path was too lossy to tell" as a clean run has been told nothing and does not know it.
    let settled = control > 0 && (discriminator > 0 || outcome.control_is_healthy());
    std::process::exit(i32::from(!settled));
}

fn format_mac(mac: &[u8; 6]) -> String {
    mac.iter()
        .map(|b| format!("{b:02X}"))
        .collect::<Vec<_>>()
        .join(":")
}
