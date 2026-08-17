//! Listens on both adapters for the protocols a switch announces itself with, and says what it
//! heard.
//!
//! The passive half of topology detection: LLDP, CDP and STP are things a managed device says about
//! itself, and a cable says nothing ever. Anything heard here that this machine did not send is a
//! device on the segment.
//!
//! **Positive-only, and no amount of waiting changes that.** An unmanaged switch has no management
//! plane to speak from - the reference NETGEAR GS308 emits nothing at all - so silence is exactly
//! as consistent with a switch as with a cable. This prints what it heard and how long it listened,
//! and leaves "nothing" as the non-answer it is.
//!
//! This binary exists because its measurements were already being quoted. A 160-second listen, its
//! per-NIC frame counts, and the `pcap_setdirection` result were recorded in a commit message and
//! run with an ad-hoc harness that was never committed - so the numbers could not be reproduced
//! from the repository by anyone, including their author. A measurement whose instrument is not in
//! the tree is an anecdote.

use std::env;
use std::time::Duration;

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::passive::{self, HONEST_SILENCE_SECONDS};
use ethlink_engine::sweep;

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 5 {
        eprintln!("usage: passivecheck <guid-a> <guid-b> <mac-a> <mac-b> [seconds]");
        eprintln!();
        eprintln!(
            "  Both MACs are excluded from both captures. Npcap hands a handle the frames the"
        );
        eprintln!("  host itself sent on that adapter, and Windows ships an LLDP agent enabled by");
        eprintln!("  default, so without this the machine reads as a switch on its own segment.");
        std::process::exit(2);
    }
    require_npcap();

    let macs = [
        mac(&args[3]).expect("bad MAC for adapter A"),
        mac(&args[4]).expect("bad MAC for adapter B"),
    ];

    let seconds: u64 = args
        .get(5)
        .map(|s| s.parse().expect("seconds must be a number"))
        .unwrap_or(HONEST_SILENCE_SECONDS);

    let devices = vec![
        device(&args[1]).expect("no device matching the first GUID"),
        device(&args[2]).expect("no device matching the second GUID"),
    ];

    println!("  A  {}", devices[0]);
    println!("  B  {}", devices[1]);
    println!("  filter {}", passive::all_excluding_self(&macs));
    println!("\nlistening {seconds}s on both adapters");

    let heard = match sweep::listen(&devices, &macs, Duration::from_secs(seconds)) {
        Ok(heard) => heard,
        Err(e) => {
            eprintln!("\nlisten failed: {e}");
            std::process::exit(1);
        }
    };

    println!(
        "\n{:<40} {:>6} {:>6} {:>6} {:>8}",
        "adapter", "LLDP", "STP", "CDP", "other"
    );
    for (device, one) in devices.iter().zip(&heard) {
        println!(
            "{:<40} {:>6} {:>6} {:>6} {:>8}",
            // Npcap device names are the interface GUID behind a fixed prefix, and the prefix is
            // the same on every row.
            device.trim_start_matches("\\Device\\NPF_"),
            one.lldp,
            one.stp,
            one.cdp,
            one.unclassified
        );
    }

    let announced: u32 = heard.iter().map(sweep::Heard::total).sum();
    let unclassified: u32 = heard.iter().map(|h| h.unclassified).sum();

    println!(
        "\nVERDICT  : {}",
        if announced > 0 {
            let which: Vec<&str> = [
                (heard.iter().map(|h| h.lldp).sum::<u32>(), "LLDP"),
                (heard.iter().map(|h| h.stp).sum::<u32>(), "STP"),
                (heard.iter().map(|h| h.cdp).sum::<u32>(), "CDP"),
            ]
            .into_iter()
            .filter(|(count, _)| *count > 0)
            .map(|(_, name)| name)
            .collect();

            format!(
                "SOMETHING IS ANNOUNCING ITSELF - heard {} from a source that is not\n           \
                 this machine. A cable does not speak {}.",
                which.join(" and "),
                which.join(" or ")
            )
        } else if seconds < HONEST_SILENCE_SECONDS {
            format!(
                "NOTHING HEARD, AND NOT FOR LONG ENOUGH - {seconds}s is under the \
                 {HONEST_SILENCE_SECONDS}s\n           this needs to outlast three CDP intervals. \
                 Silence here is about the\n           waiting, not about the segment."
            )
        } else {
            "NOTHING HEARD - which settles nothing. An unmanaged switch has no management\n           \
             plane and says nothing at all, so this looks the same as a bare cable. This\n           \
             signal can only ever prove a bridge, never a cable."
                .to_owned()
        }
    );

    if unclassified > 0 {
        println!(
            "\n{unclassified} frames passed the kernel filter and did not classify, which is worth\n\
             looking at - the filter and the classifier are supposed to agree."
        );
    }

    // Zero is the expected outcome on a direct rig, so it must not be a failure exit. Non-zero
    // means something was heard, which is the finding.
    std::process::exit(i32::from(announced > 0));
}
