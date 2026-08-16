//! Listening for the protocols a switch announces itself with: LLDP, CDP and STP.
//!
//! A managed switch usually says something. A cable never does. So anything heard here that this
//! machine did not send is a device on the segment.
//!
//! **Positive-only, and the asymmetry is not a tuning problem.** An unmanaged switch has no
//! management plane to speak from - the reference NETGEAR GS308 emits nothing at all, and no
//! observation window makes silence mean anything on such a device. Hearing LLDP proves a bridge;
//! hearing nothing proves nothing, however long you wait.
//!
//! The filter strings below were compiled and executed against Npcap 1.88 / libpcap 1.10.6 rather
//! than copied from documentation, because two of the obvious spellings are wrong in ways that fail
//! silently. See [`CDP`] and [`LLDP`].

/// LLDP: EtherType `0x88CC`, plain Ethernet II.
///
/// **Not `ether proto \lldp`.** libpcap only added the `lldp` name to its protocol table in 1.11.0
/// and Npcap 1.88 ships 1.10.6, so that spelling fails to compile with `unknown ether proto
/// 'lldp'` - while appearing in every current `pcap-filter(7)` man page. The numeric form works
/// everywhere.
pub const LLDP: &str = "ether proto 0x88cc";

/// LLDP including 802.1Q-tagged frames.
///
/// A tag shifts the EtherType by four bytes, so the plain filter misses tagged LLDP entirely -
/// verified. Destination-MAC filters do not have this problem because the MAC is at a fixed offset,
/// which is the trade if a wider net is preferred.
pub const LLDP_TAGGED: &str = "ether proto 0x88cc or (vlan and ether proto 0x88cc)";

/// STP, RSTP and MSTP alike: an 802.3 length followed by LLC DSAP/SSAP `0x42`.
///
/// `stp` is one of only three names libpcap special-cases into an LLC SAP check rather than an
/// EtherType comparison - `iso` and `netbeui` are the others - so the short spelling really does
/// work here even though the CDP equivalent does not. This form checks both SAP bytes rather than
/// the DSAP alone, which is what the bare `stp` compiles to.
pub const STP: &str = "ether[12:2] <= 1500 and ether[14:2] = 0x4242";

/// CDP: 802.3 length, LLC/SNAP, Cisco OUI `00-00-0C`, protocol id `0x2000`.
///
/// **Not `ether proto 0x2000`.** That compiles cleanly and matches nothing, ever. libpcap treats a
/// protocol value above 1500 as an EtherType and compares `ether[12:2]` against it - but CDP is
/// LLC/SNAP encapsulated, so those two bytes hold an 802.3 *length*, which is always below 1500.
/// Verified: four instructions, zero matches, including against a real CDP frame. This is the
/// failure the brief anticipated, and it is the reason these strings were executed rather than
/// reasoned about.
///
/// `ether[16:4]` covers the LLC control byte and the three-byte OUI in one load.
pub const CDP: &str = "ether[12:2] <= 1500 and ether[14:2] = 0xaaaa \
                       and ether[16:4] = 0x0300000c and ether[20:2] = 0x2000";

/// All three in one capture.
pub const ALL: &str = "ether proto 0x88cc or (ether[12:2] <= 1500 and (ether[14:2] = 0x4242 \
                       or (ether[14:2] = 0xaaaa and ether[16:4] = 0x0300000c \
                       and ether[20:2] = 0x2000)))";

/// [`ALL`], with this machine's own adapters excluded.
///
/// **Required, not an optimisation.** Npcap hands a capture handle the frames the host itself
/// transmits on that adapter - verified on this rig, where an injected frame came straight back on
/// the sending handle and every Windows DHCP broadcast appeared on both NICs' handles. Without this
/// exclusion the host's own traffic reads as a device on the segment.
///
/// `pcap_setdirection` would be the clean fix and Npcap does not implement it: it returns -1 with
/// "Setting direction is not supported on this device" for all three values, verified on a live
/// Ethernet handle. Source-MAC exclusion is the only mechanism available.
///
/// This matters beyond stray broadcasts. Windows can originate LLDP itself when Data Center
/// Bridging is installed, and several vendor NIC suites ship their own LLDP agents - in which case
/// the host becomes a false positive for "a switch is present" unless its own frames are excluded.
pub fn all_excluding_self(macs: &[[u8; 6]]) -> String {
    let mut filter = String::from(ALL);

    for mac in macs {
        filter.push_str(" and not ether src ");
        for (index, byte) in mac.iter().enumerate() {
            if index > 0 {
                filter.push(':');
            }
            filter.push_str(&format!("{byte:02x}"));
        }
    }

    filter
}

/// How long silence has to last before it is worth reporting, in seconds.
///
/// LLDP announces every 30 s, STP hello is 2 s, CDP is 60 s with a 180 s hold. Three CDP intervals
/// plus margin is what makes "heard nothing" a statement about the segment rather than about
/// patience - and even then it is only a statement about *managed* devices, since an unmanaged
/// switch never says anything at all.
///
/// Link-up triggers immediate transmission in almost every implementation, and LLDP-MED adds a
/// fast-start burst, so a probe that can bounce the link collapses this wait to about a second.
pub const HONEST_SILENCE_SECONDS: u64 = 190;

/// The shorter window, when only LLDP and STP are being asserted on.
pub const LLDP_AND_STP_SECONDS: u64 = 35;

/// Silence is only evidence after three CDP intervals, and even then only about managed devices.
///
/// Compile-time, because these are constants: shortening one below the interval it exists to
/// outlast should fail the build rather than a test run.
const _: () = assert!(
    HONEST_SILENCE_SECONDS >= 180,
    "silence shorter than three CDP intervals is impatience, not evidence"
);
const _: () = assert!(
    LLDP_AND_STP_SECONDS >= 30,
    "the short window must still outlast one LLDP interval"
);

#[cfg(test)]
mod tests {
    use super::*;

    /// The two spellings that compile and never match. Pinned as strings because the defect is
    /// invisible at runtime - a filter that matches nothing looks exactly like a quiet link.
    #[test]
    fn the_filters_avoid_the_two_silent_failures() {
        assert!(
            !LLDP.contains("\\lldp"),
            "libpcap 1.10.6 has no 'lldp' protocol name; Npcap 1.88 ships 1.10.6"
        );
        assert!(
            !CDP.contains("ether proto 0x2000"),
            "CDP is LLC/SNAP, so ether[12:2] holds a length and can never equal 0x2000"
        );
    }

    #[test]
    fn cdp_matches_the_snap_header_rather_than_an_ethertype() {
        assert!(CDP.contains("ether[12:2] <= 1500"));
        assert!(CDP.contains("0xaaaa"), "LLC DSAP and SSAP");
        assert!(CDP.contains("0x0300000c"), "LLC control plus Cisco OUI");
        assert!(CDP.contains("0x2000"), "SNAP protocol id");
    }

    #[test]
    fn stp_checks_both_sap_bytes() {
        assert!(STP.contains("0x4242"));
    }

    #[test]
    fn the_combined_filter_covers_all_three() {
        assert!(ALL.contains("0x88cc"), "LLDP");
        assert!(ALL.contains("0x4242"), "STP");
        assert!(ALL.contains("0x2000"), "CDP");
    }

    #[test]
    fn self_exclusion_names_every_adapter() {
        let filter = all_excluding_self(&[
            [0x18, 0xDB, 0xF2, 0x4D, 0xBB, 0xEE],
            [0x00, 0xE0, 0x4C, 0x68, 0x06, 0xCA],
        ]);

        assert!(filter.contains("not ether src 18:db:f2:4d:bb:ee"));
        assert!(filter.contains("not ether src 00:e0:4c:68:06:ca"));
        assert!(filter.starts_with(ALL));
    }

    #[test]
    fn self_exclusion_with_no_adapters_is_the_plain_filter() {
        assert_eq!(all_excluding_self(&[]), ALL);
    }
}
