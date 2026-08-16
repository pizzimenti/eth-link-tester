//! The reserved-multicast sweep: which link-local addresses cross between two adapters.
//!
//! IEEE 802.1Q makes each reserved address a permanent filtering-database entry that management
//! cannot remove, so a conforming relay component drops them rather than forwarding them. Send one
//! to each and see what arrives, and the pattern says whether anything is bridging.
//!
//! That is the theory. The silicon is less obliging, and which address is chosen decides whether
//! this works at all - see [`ProbeAddress`].

/// One destination in the sweep, with the reason it is in it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ProbeAddress {
    /// Destination MAC.
    pub mac: [u8; 6],
    /// Short name, used in output and matched by the managed side.
    pub name: &'static str,
    /// True when filtering this one is evidence; false when it is a corroborator or the control.
    pub discriminating: bool,
}

/// `01:80:C2:00:00:02`, Slow Protocols. The one that decides the answer.
///
/// Filtered by every 802.1Q relay component type - C-VLAN, S-VLAN and TPMR alike - and specified
/// as always-filtered and non-overridable in both Realtek datasheet generations. If this crosses,
/// nothing conforming to 802.1 is relaying.
pub const SLOW_PROTOCOLS: ProbeAddress = ProbeAddress {
    mac: [0x01, 0x80, 0xC2, 0x00, 0x00, 0x02],
    name: "SlowProtocols",
    discriminating: true,
};

/// `01:80:C2:00:00:04`, MAC-specific Control Protocols. The safe corroborator.
///
/// Filtered by every component type, and the only address in the block with no protocol assigned
/// in practice - nothing anywhere is listening for it, which makes it the safest frame here to put
/// on a live network. Realtek forwards it by default, so it cannot decide anything on its own.
pub const MAC_CONTROL: ProbeAddress = ProbeAddress {
    mac: [0x01, 0x80, 0xC2, 0x00, 0x00, 0x04],
    name: "MacControlProtocols",
    discriminating: false,
};

/// `01:80:C2:00:00:0E`, Nearest Bridge. The middle signal.
///
/// Carries the strongest wording in the IEEE listing - no relay device will be defined that
/// forwards it - and still leaks through cheap switches often enough to be documented. The gap
/// between the intent and the silicon is the point of sending it.
pub const NEAREST_BRIDGE: ProbeAddress = ProbeAddress {
    mac: [0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E],
    name: "NearestBridge",
    discriminating: false,
};

/// A locally-administered group address outside the reserved block, which nothing may filter.
///
/// **Not optional.** Without it there is no way to separate "a bridge filtered my probe" from "the
/// receiving adapter never handed it up" - a promiscuous mode the miniport declined, a MAC that
/// swallowed the address, a link still negotiating. All of those produce exactly the silence a
/// conforming bridge produces.
///
/// Bit 0 of the first octet is the group bit and bit 1 is the locally-administered bit, so a first
/// octet of `0x03` has both set: a group address that no vendor owns and no standard assigns.
pub const CONTROL: ProbeAddress = ProbeAddress {
    mac: [0x03, 0x00, 0x5E, 0x00, 0x00, 0x01],
    name: "Control",
    discriminating: false,
};

/// The sweep, control first so a broken path is discovered before anything is concluded.
pub const SWEEP: [ProbeAddress; 4] = [CONTROL, SLOW_PROTOCOLS, MAC_CONTROL, NEAREST_BRIDGE];

/// Finds the sweep entry a captured frame was addressed to.
pub fn addressed_to(destination: &[u8]) -> Option<ProbeAddress> {
    if destination.len() < 6 {
        return None;
    }

    SWEEP
        .into_iter()
        .find(|probe| probe.mac == destination[..6])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_reserved_probe_is_in_the_link_local_block() {
        for probe in [SLOW_PROTOCOLS, MAC_CONTROL, NEAREST_BRIDGE] {
            assert_eq!(
                probe.mac[..5],
                [0x01, 0x80, 0xC2, 0x00, 0x00],
                "{} is not in 01:80:C2:00:00:00-0F",
                probe.name
            );
        }
    }

    /// The plan called for `01:80:C2:00:00:00`. It is filtered only by MAC Bridge and C-VLAN
    /// components, and Realtek's silicon forwards it by default, so a detector resting on it
    /// reports a direct cable through a common unmanaged switch.
    ///
    /// Compared as a whole address, not by its last octet. The first version of this test checked
    /// `mac[5]` alone, which is a different claim: the control address ends `:01` for reasons that
    /// have nothing to do with PAUSE, and the test failed on it while saying something true.
    #[test]
    fn the_sweep_avoids_the_bridge_group_address() {
        const BRIDGE_GROUP: [u8; 6] = [0x01, 0x80, 0xC2, 0x00, 0x00, 0x00];

        assert!(
            !SWEEP.iter().any(|probe| probe.mac == BRIDGE_GROUP),
            "01:80:C2:00:00:00 is forwarded by default on common silicon and must not be probed"
        );
    }

    /// `01:80:C2:00:00:01` is the PAUSE address, which several NIC MACs consume by destination
    /// alone - which is why capturing pause frames on Windows is a known nuisance.
    #[test]
    fn the_sweep_avoids_the_pause_address() {
        const PAUSE: [u8; 6] = [0x01, 0x80, 0xC2, 0x00, 0x00, 0x01];

        assert!(!SWEEP.iter().any(|probe| probe.mac == PAUSE));
    }

    #[test]
    fn the_control_is_outside_the_reserved_block_and_is_a_group_address() {
        assert_ne!(CONTROL.mac[..3], [0x01, 0x80, 0xC2]);
        assert_eq!(CONTROL.mac[0] & 0x01, 0x01, "must be a group address");
        assert_eq!(CONTROL.mac[0] & 0x02, 0x02, "must be locally administered");
    }

    #[test]
    fn exactly_one_probe_decides_the_answer() {
        assert_eq!(SWEEP.iter().filter(|p| p.discriminating).count(), 1);
    }

    #[test]
    fn the_control_is_swept_first() {
        assert_eq!(SWEEP[0].name, CONTROL.name);
    }

    #[test]
    fn a_captured_frame_is_matched_to_its_address() {
        assert_eq!(addressed_to(&SLOW_PROTOCOLS.mac), Some(SLOW_PROTOCOLS));
        assert_eq!(addressed_to(&CONTROL.mac), Some(CONTROL));
        assert_eq!(addressed_to(&[0x01, 0x80, 0xC2, 0x00, 0x00, 0x00]), None);
        assert_eq!(addressed_to(&[0x01, 0x02]), None);
    }
}
