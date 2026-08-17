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
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::passive::{self, HONEST_SILENCE_SECONDS};

/// What a captured frame turned out to be.
#[derive(Clone, Copy, PartialEq, Eq)]
enum Protocol {
    Lldp,
    Stp,
    Cdp,
}

impl Protocol {
    const ALL: [Protocol; 3] = [Protocol::Lldp, Protocol::Stp, Protocol::Cdp];

    fn name(self) -> &'static str {
        match self {
            Protocol::Lldp => "LLDP",
            Protocol::Stp => "STP",
            Protocol::Cdp => "CDP",
        }
    }

    /// Classifies a frame the kernel filter already accepted.
    ///
    /// The same three shapes the filter matches on, read again here because the filter says only
    /// that a frame is one of the three and a report has to say which.
    fn of(frame: &[u8]) -> Option<Protocol> {
        if frame.len() < 22 {
            return None;
        }

        let ethertype = u16::from_be_bytes([frame[12], frame[13]]);
        if ethertype == 0x88CC {
            return Some(Protocol::Lldp);
        }
        if ethertype > 1500 {
            return None;
        }

        match (frame[14], frame[15]) {
            (0x42, 0x42) => Some(Protocol::Stp),
            (0xAA, 0xAA)
                if frame[16..20] == [0x03, 0x00, 0x00, 0x0C]
                    && frame[20..22] == [0x20, 0x00] =>
            {
                Some(Protocol::Cdp)
            }
            _ => None,
        }
    }
}

/// One adapter's tally.
struct Heard {
    adapter: String,
    counts: [u32; 3],
    unclassified: u32,
}

impl Heard {
    fn total(&self) -> u32 {
        self.counts.iter().sum::<u32>() + self.unclassified
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 5 {
        eprintln!("usage: passivecheck <guid-a> <guid-b> <mac-a> <mac-b> [seconds]");
        eprintln!();
        eprintln!(
            "  Both MACs are excluded from both captures. Npcap hands a handle the frames the"
        );
        eprintln!(
            "  host itself sent on that adapter, and Windows ships an LLDP agent enabled by"
        );
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

    let filter = passive::all_excluding_self(&macs);

    let names = [
        device(&args[1]).expect("no device matching the first GUID"),
        device(&args[2]).expect("no device matching the second GUID"),
    ];

    println!("  A  {}", names[0]);
    println!("  B  {}", names[1]);
    println!("  filter {filter}");
    println!("\nlistening {seconds}s on both adapters");

    let running = Arc::new(AtomicBool::new(true));
    let deadline = Instant::now() + Duration::from_secs(seconds);

    let listeners: Vec<_> = names
        .into_iter()
        .map(|name| {
            let filter = filter.clone();
            let running = Arc::clone(&running);
            std::thread::spawn(move || listen(name, &filter, &running))
        })
        .collect();

    // Ticks a progress line rather than going silent for three minutes, because the honest window
    // is long enough that a quiet console is indistinguishable from a hang.
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_secs(1));
        let left = deadline.saturating_duration_since(Instant::now()).as_secs();
        print!("\r  {left}s remaining   ");
        use std::io::Write;
        let _ = std::io::stdout().flush();
    }
    running.store(false, Ordering::Release);
    println!("\r                    ");

    let heard: Vec<Heard> = listeners.into_iter().filter_map(|h| h.join().ok()).collect();

    println!("{:<40} {:>6} {:>6} {:>6} {:>8}", "adapter", "LLDP", "STP", "CDP", "other");
    for one in &heard {
        println!(
            "{:<40} {:>6} {:>6} {:>6} {:>8}",
            // Npcap device names are the interface GUID behind a fixed prefix, and the prefix is
            // the same on every row.
            one.adapter.trim_start_matches("\\Device\\NPF_"),
            one.counts[0],
            one.counts[1],
            one.counts[2],
            one.unclassified
        );
    }

    let total: u32 = heard.iter().map(Heard::total).sum();
    let announced: u32 = heard.iter().map(|h| h.counts.iter().sum::<u32>()).sum();

    println!(
        "\nVERDICT  : {}",
        if announced > 0 {
            let which: Vec<&str> = Protocol::ALL
                .iter()
                .enumerate()
                .filter(|(index, _)| heard.iter().any(|h| h.counts[*index] > 0))
                .map(|(_, protocol)| protocol.name())
                .collect();

            format!(
                "SOMETHING IS ANNOUNCING ITSELF - heard {} from a source that is not\n           \
                 this machine. A cable does not speak {}.",
                which.join(" and "),
                which.join(" or ")
            )
        } else if seconds < HONEST_SILENCE_SECONDS {
            format!(
                "NOTHING HEARD, AND NOT FOR LONG ENOUGH - {seconds}s is under the {HONEST_SILENCE_SECONDS}s\n           \
                 this needs to outlast three CDP intervals. Silence here is about the\n           \
                 waiting, not about the segment."
            )
        } else {
            "NOTHING HEARD - which settles nothing. An unmanaged switch has no management\n           \
             plane and says nothing at all, so this looks the same as a bare cable. This\n           \
             signal can only ever prove a bridge, never a cable."
                .to_owned()
        }
    );

    println!("\n{total} frames matched the filter in total; anything under 'other' passed the\n\
              kernel filter and did not classify, which is worth looking at.");

    // Zero is the expected outcome on a direct rig, so it must not be a failure exit. Non-zero
    // means something was heard, which is the finding.
    std::process::exit(i32::from(announced > 0));
}

fn listen(adapter: String, filter: &str, running: &AtomicBool) -> Heard {
    let mut heard = Heard {
        adapter: adapter.clone(),
        counts: [0; 3],
        unclassified: 0,
    };

    // Promiscuous, because LLDP and STP go to group addresses this adapter has not joined and the
    // MAC would drop them at the hardware filter. A short timeout so the loop notices the deadline.
    let mut capture = match pcap::Capture::from_device(adapter.as_str())
        .and_then(|c| c.promisc(true).immediate_mode(true).timeout(200).open())
    {
        Ok(capture) => capture,
        Err(e) => {
            eprintln!("could not open {adapter}: {e}");
            return heard;
        }
    };

    if let Err(e) = capture.filter(filter, true) {
        eprintln!("filter did not compile on {adapter}: {e}");
        return heard;
    }

    while running.load(Ordering::Acquire) {
        match capture.next_packet() {
            Ok(packet) => match Protocol::of(packet.data) {
                Some(protocol) => {
                    let index = Protocol::ALL.iter().position(|p| *p == protocol).unwrap_or(0);
                    heard.counts[index] += 1;
                }
                None => heard.unclassified += 1,
            },
            Err(pcap::Error::TimeoutExpired) => {}
            Err(e) => {
                eprintln!("capture error on {adapter}: {e}");
                break;
            }
        }
    }

    heard
}
