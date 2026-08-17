//! Running the reserved-multicast sweep and the passive listen, once, for every caller.
//!
//! Both measurements started life inside a diagnostic binary, which was the right place to prove
//! them and the wrong place to leave them: the managed host needs the same two answers, and a
//! second implementation of "send twenty frames to each address and count what arrives" is a second
//! thing that can disagree with the first. The binaries are now thin wrappers over this, so what
//! `topocheck` prints and what the app reports come from the same code.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::frame;
use crate::topology::SWEEP;

/// Frames per address. A handful, because one lost frame must not read as a filtered one.
pub const DEFAULT_REPEATS: u32 = 20;

/// How long to wait for the sweep to arrive once it has all been sent.
const SETTLE: Duration = Duration::from_millis(500);

/// How long a capture read blocks before the loop checks whether it should stop.
const READ_TIMEOUT_MS: i32 = 200;

/// What arrived, per sweep address, in [`SWEEP`] order.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SweepOutcome {
    /// Frames sent to each address.
    pub sent: u32,
    /// Frames captured at the far adapter, indexed as [`SWEEP`] is.
    pub arrived: [u32; SWEEP.len()],
}

impl SweepOutcome {
    /// The control's count, which gates everything else.
    pub fn control(&self) -> u32 {
        self.arrived[0]
    }

    /// The discriminating address's count, or zero if the sweep somehow has none.
    pub fn discriminator(&self) -> u32 {
        SWEEP
            .iter()
            .position(|probe| probe.discriminating)
            .map(|index| self.arrived[index])
            .unwrap_or(0)
    }

    /// Whether the control delivered well enough for the discriminator's silence to mean anything.
    ///
    /// Three quarters, matching `ReservedMulticastProbe.MinimumControlDelivery` on the managed side.
    /// One frame in twenty used to be enough, which on a link losing 95% of frames lets all twenty
    /// discriminator frames vanish by chance about a third of the time - reporting a marginal cable
    /// as a bridge.
    pub fn control_is_healthy(&self) -> bool {
        self.control() * 4 >= self.sent * 3
    }
}

/// Sends the sweep from one adapter and counts what reaches the other.
///
/// The receiver is opened before anything is sent, and promiscuous mode is not optional: none of
/// these group addresses is in the adapter's multicast list, so the NIC would drop every one of
/// them at its hardware filter.
///
/// `run_id` must be distinct per sweep rather than per process. Frames from a previous sweep can
/// still be sitting in an NPF buffer, and sharing an id would count them as this sweep's arrivals -
/// the exact false "crossed" the run id exists to prevent.
pub fn run(
    tx_device: &str,
    rx_device: &str,
    tx_mac: [u8; 6],
    repeats: u32,
    run_id: u16,
) -> Result<SweepOutcome, pcap::Error> {
    let mut rx = pcap::Capture::from_device(rx_device)?
        .promisc(true)
        .immediate_mode(true)
        .timeout(READ_TIMEOUT_MS)
        .open()?;

    rx.filter(&frame::filter(run_id), true)?;

    let mut tx = pcap::Capture::from_device(tx_device)?.open()?;

    let mut seq = 0u32;
    for probe in SWEEP {
        // Built per address, because the destination is the whole experiment. No VLAN tag: Npcap
        // strips one and re-applies it as NDIS out-of-band metadata, so a tagged probe does not
        // reach the wire as written.
        let mut buffer = frame::build(probe.mac, tx_mac, frame::MIN_BUFFER, run_id);

        for _ in 0..repeats {
            frame::stamp(&mut buffer, seq, frame::UNTIMED);
            seq = seq.wrapping_add(1);
            tx.sendpacket(buffer.as_slice())?;
        }
    }

    let mut arrived = [0u32; SWEEP.len()];
    let deadline = Instant::now() + SETTLE;

    while Instant::now() < deadline {
        match rx.next_packet() {
            Ok(packet) => {
                if frame::parse(packet.data, run_id).is_none() {
                    continue;
                }
                if let Some(probe) = crate::topology::addressed_to(packet.data) {
                    if let Some(index) = SWEEP.iter().position(|p| p.name == probe.name) {
                        arrived[index] += 1;
                    }
                }
            }
            Err(pcap::Error::TimeoutExpired) => continue,
            Err(e) => return Err(e),
        }
    }

    Ok(SweepOutcome {
        sent: repeats,
        arrived,
    })
}

/// What a passive listen heard, per protocol.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Heard {
    pub lldp: u32,
    pub stp: u32,
    pub cdp: u32,
    /// Passed the kernel filter and did not classify, which is worth looking at.
    pub unclassified: u32,
}

impl Heard {
    pub fn total(&self) -> u32 {
        self.lldp + self.stp + self.cdp
    }

    fn add(&mut self, protocol: Protocol) {
        match protocol {
            Protocol::Lldp => self.lldp += 1,
            Protocol::Stp => self.stp += 1,
            Protocol::Cdp => self.cdp += 1,
        }
    }
}

/// What a captured frame turned out to be.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Protocol {
    Lldp,
    Stp,
    Cdp,
}

impl Protocol {
    /// Classifies a frame the kernel filter already accepted.
    ///
    /// The same three shapes the filter matches on, read again here because the filter says only
    /// that a frame is one of the three and a report has to say which.
    pub fn of(frame: &[u8]) -> Option<Protocol> {
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
                if frame[16..20] == [0x03, 0x00, 0x00, 0x0C] && frame[20..22] == [0x20, 0x00] =>
            {
                Some(Protocol::Cdp)
            }
            _ => None,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Protocol::Lldp => "LLDP",
            Protocol::Stp => "STP",
            Protocol::Cdp => "CDP",
        }
    }
}

/// Listens on every named device for `duration`, excluding every named MAC as a source.
///
/// One thread per device, because a switch may announce itself on either. The exclusion covers all
/// of them rather than just the local one: Npcap hands a capture handle the frames the host itself
/// transmitted on that adapter, so without it a host running any LLDP agent reads as a device on its
/// own segment.
///
/// Per-device totals are summed. Which port heard a bridge is a detail the binary prints and the
/// verdict does not need - a device on either segment is a device in the path.
pub fn listen(
    devices: &[String],
    macs: &[[u8; 6]],
    duration: Duration,
) -> Result<Vec<Heard>, pcap::Error> {
    let filter = crate::passive::all_excluding_self(macs);
    let running = Arc::new(AtomicBool::new(true));
    let deadline = Instant::now() + duration;

    let listeners: Vec<_> = devices
        .iter()
        .map(|device| {
            let device = device.clone();
            let filter = filter.clone();
            let running = Arc::clone(&running);

            std::thread::spawn(move || listen_one(&device, &filter, &running))
        })
        .collect();

    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(100));
    }
    running.store(false, Ordering::Release);

    let mut heard = Vec::with_capacity(listeners.len());
    for listener in listeners {
        // A panicked listener is a bug, not a quiet zero: reporting silence it never observed would
        // turn a broken instrument into evidence about the segment.
        match listener.join() {
            Ok(result) => heard.push(result?),
            Err(_) => return Err(pcap::Error::PcapError("a capture thread panicked".to_owned())),
        }
    }

    Ok(heard)
}

fn listen_one(
    device: &str,
    filter: &str,
    running: &AtomicBool,
) -> Result<Heard, pcap::Error> {
    // Promiscuous, because LLDP and STP go to group addresses this adapter has not joined and the
    // MAC would drop them at its hardware filter.
    let mut capture = pcap::Capture::from_device(device)?
        .promisc(true)
        .immediate_mode(true)
        .timeout(READ_TIMEOUT_MS)
        .open()?;

    capture.filter(filter, true)?;

    let mut heard = Heard::default();

    while running.load(Ordering::Acquire) {
        match capture.next_packet() {
            Ok(packet) => match Protocol::of(packet.data) {
                Some(protocol) => heard.add(protocol),
                None => heard.unclassified += 1,
            },
            Err(pcap::Error::TimeoutExpired) => {}
            Err(e) => return Err(e),
        }
    }

    Ok(heard)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame_with(tail: &[u8]) -> Vec<u8> {
        let mut bytes = vec![0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E, 0, 0, 0, 0, 0, 1];
        bytes.extend_from_slice(tail);
        bytes.resize(60, 0);
        bytes
    }

    #[test]
    fn each_protocol_is_recognised_by_its_own_shape() {
        assert_eq!(Protocol::of(&frame_with(&[0x88, 0xCC])), Some(Protocol::Lldp));
        assert_eq!(
            Protocol::of(&frame_with(&[0x00, 0x26, 0x42, 0x42])),
            Some(Protocol::Stp)
        );
        assert_eq!(
            Protocol::of(&frame_with(&[
                0x00, 0x26, 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00
            ])),
            Some(Protocol::Cdp)
        );
    }

    /// IPv4 and a runt classify as nothing, rather than as whatever the first branch happens to be.
    #[test]
    fn anything_else_classifies_as_nothing() {
        assert_eq!(Protocol::of(&frame_with(&[0x08, 0x00])), None);
        assert_eq!(Protocol::of(&[0x01, 0x80]), None);
    }

    /// An 802.3 length that happens to equal an EtherType must not be read as one.
    #[test]
    fn a_length_field_is_not_an_ethertype() {
        assert_eq!(Protocol::of(&frame_with(&[0x05, 0xDC, 0x00, 0x00])), None);
    }

    /// The gate the managed side applies to the same numbers, so both agree what "healthy" means.
    #[test]
    fn the_control_gate_matches_three_quarters() {
        let outcome = |control: u32| SweepOutcome {
            sent: 20,
            arrived: [control, 0, 0, 0],
        };

        assert!(outcome(20).control_is_healthy());
        assert!(outcome(15).control_is_healthy());
        assert!(!outcome(14).control_is_healthy());
        assert!(!outcome(1).control_is_healthy());
    }

    #[test]
    fn the_discriminator_is_read_from_the_flag_not_a_fixed_index() {
        let index = SWEEP.iter().position(|p| p.discriminating).expect("one decides");
        let mut arrived = [0u32; SWEEP.len()];
        arrived[index] = 7;

        assert_eq!(SweepOutcome { sent: 20, arrived }.discriminator(), 7);
    }
}
