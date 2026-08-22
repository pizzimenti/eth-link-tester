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
///
/// **Built from the three constants rather than re-typed.** It used to be a fourth hand-factored
/// expression restating all of them, in a module whose entire subject is that a BPF filter can
/// compile cleanly and match nothing - so a fix to one sub-filter that missed this copy would
/// reproduce the module's own documented worst failure, and look exactly like a quiet link. The
/// factoring the hand-written version had is not lost: libpcap's optimiser recovers it, and the
/// two forms were confirmed to behave identically on every frame in
/// [`the_combined_filter_matches_all_three_and_nothing_else`].
///
/// **Deliberately the plain [`LLDP`], not [`LLDP_TAGGED`], so tagged LLDP is invisible here.** That
/// is a trade rather than an oversight, and it is not a small one: libpcap's `vlan` keyword is not
/// a predicate but a compile-time offset shift applied to every term that follows it, and
/// parentheses do not reliably contain it - so a tagged term in front of [`STP`] and [`CDP`] moves
/// their absolute `ether[12:2]` reads four bytes along and silently breaks both. A separate listen
/// with [`LLDP_TAGGED`] is the way to cover tagged frames; folding it in here would trade two
/// working protocol filters for one.
pub fn all() -> String {
    format!("({LLDP}) or ({STP}) or ({CDP})")
}

/// [`all`], with this machine's own adapters excluded.
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
/// This matters beyond stray broadcasts. Windows can originate LLDP itself, and several vendor NIC
/// suites ship their own LLDP agents - in which case the host becomes a false positive for "a
/// switch is present" unless its own frames are excluded.
///
/// **How real that is on this rig, measured rather than assumed:** `ms_lldp`, the Microsoft LLDP
/// Protocol Driver, is bound and *enabled* on both reference adapters - and forty seconds of
/// listening with no exclusion at all, against an LLDP interval of thirty, heard nothing on either.
/// An enabled binding is not a transmitting agent; Windows' `mslldp.sys` is there principally to
/// receive. So the exclusion is defence against a class of host this rig is not a member of, which
/// is a reason to keep it and not a reason to call it load-bearing here. Worth knowing when a
/// listen on someone else's machine does hear something.
pub fn all_excluding_self(macs: &[[u8; 6]]) -> String {
    // Parenthesised, so the exclusions apply to the whole set rather than resting on libpcap giving
    // `and` and `or` equal precedence and left associativity. They do, and the unparenthesised form
    // was verified correct - but the correctness of a filter that rejects everyone's frames and one
    // that rejects nobody's look identical from here, so it is not a thing to leave to precedence.
    let mut filter = format!("({})", all());

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

    /// Ethernet frames built by hand, so a filter can be run against known contents.
    ///
    /// Substring assertions cannot see the failure this module exists to prevent. `ether proto
    /// 0x2000` contains "0x2000" and matches no CDP frame that has ever existed; a combined filter
    /// can contain all three protocol constants and still match none of them after a regrouping.
    /// The only test that distinguishes a working filter from a plausible one is running it, so
    /// these tests compile each filter and push frames through it.
    mod frames {
        pub const SELF_MAC: [u8; 6] = [0x18, 0xDB, 0xF2, 0x4D, 0xBB, 0xEE];
        pub const OTHER_MAC: [u8; 6] = [0x00, 0xE0, 0x4C, 0x68, 0x06, 0xCA];

        /// Destination, source, then whatever distinguishes the protocol, padded to 60 bytes.
        fn frame(source: [u8; 6], tail: &[u8]) -> Vec<u8> {
            let mut bytes = vec![0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E];
            bytes.extend_from_slice(&source);
            bytes.extend_from_slice(tail);
            bytes.resize(60, 0);
            bytes
        }

        pub fn lldp(source: [u8; 6]) -> Vec<u8> {
            frame(source, &[0x88, 0xCC])
        }

        /// An 802.1Q tag shifts the EtherType four bytes along, which is the whole difficulty.
        pub fn tagged_lldp(source: [u8; 6]) -> Vec<u8> {
            frame(source, &[0x81, 0x00, 0x00, 0x64, 0x88, 0xCC])
        }

        /// 802.3 length, then LLC DSAP and SSAP of 0x42.
        pub fn stp(source: [u8; 6]) -> Vec<u8> {
            frame(source, &[0x00, 0x26, 0x42, 0x42, 0x03])
        }

        /// 802.3 length, LLC/SNAP, Cisco OUI, protocol id 0x2000.
        pub fn cdp(source: [u8; 6]) -> Vec<u8> {
            frame(
                source,
                &[0x00, 0x26, 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00],
            )
        }

        pub fn ipv4(source: [u8; 6]) -> Vec<u8> {
            frame(source, &[0x08, 0x00, 0x45, 0x00])
        }
    }

    /// Fails the test when Npcap is missing, rather than passing quietly.
    ///
    /// **These tests are `#[ignore]`d and that is the honest arrangement.** They need libpcap's own
    /// compiler and matcher - a reimplementation would be testing the reimplementation - and so they
    /// need `wpcap.dll`, which Npcap's licence forbids redistributing and CI therefore does not
    /// have.
    ///
    /// The previous arrangement returned early with an `eprintln!`, which was worse than it looked:
    /// libtest captures the output of *passing* tests, so the message was never printed and the
    /// seven filter tests counted as passes. `61 passed; 0 ignored` on a machine with Npcap and
    /// `61 passed; 0 ignored` on one without - byte-identical, indistinguishable, and CI has been
    /// in the second state since these tests landed. A module whose entire subject is filters that
    /// fail silently had tests that failed silently.
    ///
    /// `#[ignore]` makes the state visible in every run's summary line and impossible to fake, and
    /// this assertion makes an explicitly-requested run fail loudly rather than skip. Run them with
    /// `cargo test -- --include-ignored`, which is what the bench rig does.
    fn require_libpcap() {
        assert!(
            crate::diag::ensure_npcap(),
            "these tests exercise libpcap's real compiler and matcher, so they need Npcap              installed. They are #[ignore]d for that reason - running them explicitly on a machine              without it is a mistake, not a skip."
        );
    }

    /// Runs `filter` over `packets` and returns how many it matched.
    ///
    /// Through a capture file rather than a live device, so the assertions hold with no NIC, no
    /// cable and no capture privileges - but through libpcap's real compiler and real matcher,
    /// which is the part that has to be right.
    fn matches(filter: &str, packets: &[Vec<u8>]) -> usize {
        // A counter, not a hash of the contents. Two tests that happen to run the same filter over
        // the same number of frames are not unusual here, the harness runs them concurrently, and
        // one clobbering the other's file surfaces as a truncated capture - which reads exactly
        // like a filter defect and is not one.
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

        let path = std::env::temp_dir().join(format!(
            "ethlink-filter-{}-{}.pcap",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));

        {
            let dead = pcap::Capture::dead(pcap::Linktype::ETHERNET).expect("dead capture");
            let mut savefile = dead.savefile(&path).expect("savefile");

            for packet in packets {
                // Zeroed rather than built from a libc::timeval, which would mean taking a
                // dependency on libc to write a timestamp no filter here reads. Every field is an
                // integer, so a zeroed PacketHeader is a valid one.
                let mut header: pcap::PacketHeader = unsafe { std::mem::zeroed() };
                header.caplen = packet.len() as u32;
                header.len = packet.len() as u32;

                savefile.write(&pcap::Packet::new(&header, packet));
            }
        }

        let mut capture = pcap::Capture::from_file(&path).expect("reopen");
        capture
            .filter(filter, true)
            .unwrap_or_else(|e| panic!("filter did not compile: {filter}\n{e}"));

        let mut matched = 0;
        while capture.next_packet().is_ok() {
            matched += 1;
        }

        let _ = std::fs::remove_file(&path);
        matched
    }

    fn one_of_each(source: [u8; 6]) -> Vec<Vec<u8>> {
        vec![
            frames::lldp(source),
            frames::tagged_lldp(source),
            frames::stp(source),
            frames::cdp(source),
            frames::ipv4(source),
        ]
    }

    /// Each protocol filter matches its own protocol and nothing else in the set.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn every_filter_matches_exactly_its_own_protocol() {
        require_libpcap();

        let packets = one_of_each(frames::OTHER_MAC);

        assert_eq!(matches(LLDP, &packets), 1, "LLDP");
        assert_eq!(matches(STP, &packets), 1, "STP");
        assert_eq!(matches(CDP, &packets), 1, "CDP");
    }

    /// The two spellings that compile and match nothing, proven by running them.
    ///
    /// This is the module's whole reason for existing, and until now it was asserted by checking
    /// that the constants did not *contain* the bad spellings - which says nothing about whether
    /// what they do contain works.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn the_wrong_spellings_compile_and_match_nothing() {
        require_libpcap();

        let packets = one_of_each(frames::OTHER_MAC);

        assert_eq!(
            matches("ether proto 0x2000", &packets),
            0,
            "CDP is LLC/SNAP, so ether[12:2] holds a length and can never equal 0x2000"
        );
        assert_eq!(matches(CDP, &packets), 1, "and the SNAP form does match it");
    }

    /// The plain LLDP filter misses tagged LLDP, and the tagged one catches both.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn a_vlan_tag_hides_lldp_from_the_plain_filter() {
        require_libpcap();

        let tagged = vec![frames::tagged_lldp(frames::OTHER_MAC)];
        let both = vec![
            frames::lldp(frames::OTHER_MAC),
            frames::tagged_lldp(frames::OTHER_MAC),
        ];

        assert_eq!(matches(LLDP, &tagged), 0);
        assert_eq!(matches(LLDP_TAGGED, &both), 2);
    }

    /// The combined filter catches all three protocols and nothing else.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn the_combined_filter_matches_all_three_and_nothing_else() {
        require_libpcap();

        let packets = one_of_each(frames::OTHER_MAC);

        // LLDP, STP and CDP; not the tagged LLDP and not the IPv4 frame.
        assert_eq!(matches(&all(), &packets), 3);
        assert_eq!(matches(&all(), &[frames::ipv4(frames::OTHER_MAC)]), 0);
        assert_eq!(
            matches(&all(), &[frames::tagged_lldp(frames::OTHER_MAC)]),
            0
        );
    }

    /// Building it from the constants did not change what it matches.
    ///
    /// The hand-factored expression this replaced was correct; the finding was that it was a fourth
    /// copy nothing compared against the other three. This keeps the old form as a fixture so the
    /// replacement is a refactor and can be seen to be one.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn the_combined_filter_agrees_with_the_hand_factored_form() {
        require_libpcap();

        const HAND_FACTORED: &str = "ether proto 0x88cc or (ether[12:2] <= 1500 \
                                     and (ether[14:2] = 0x4242 or (ether[14:2] = 0xaaaa \
                                     and ether[16:4] = 0x0300000c and ether[20:2] = 0x2000)))";

        let packets = [
            one_of_each(frames::SELF_MAC),
            one_of_each(frames::OTHER_MAC),
        ]
        .concat();

        assert_eq!(matches(&all(), &packets), matches(HAND_FACTORED, &packets));
    }

    /// Self-exclusion removes this host's frames and keeps everyone else's.
    ///
    /// Load-bearing rather than tidy: Npcap hands a capture handle the frames the host itself
    /// transmits on that adapter, and Windows ships an LLDP agent enabled by default - so without
    /// this the host reads as a device on its own segment.
    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn self_exclusion_drops_this_hosts_frames_and_keeps_the_rest() {
        require_libpcap();

        let packets = [
            one_of_each(frames::SELF_MAC),
            one_of_each(frames::OTHER_MAC),
        ]
        .concat();
        let filter = all_excluding_self(&[frames::SELF_MAC]);

        assert_eq!(
            matches(&all(), &packets),
            6,
            "three protocols from each host"
        );
        assert_eq!(matches(&filter, &packets), 3, "only the other host's");
        assert_eq!(
            matches(
                &all_excluding_self(&[frames::SELF_MAC, frames::OTHER_MAC]),
                &packets
            ),
            0,
            "excluding both hosts leaves nothing"
        );
    }

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
    fn self_exclusion_names_every_adapter() {
        let filter = all_excluding_self(&[frames::SELF_MAC, frames::OTHER_MAC]);

        assert!(filter.contains("not ether src 18:db:f2:4d:bb:ee"));
        assert!(filter.contains("not ether src 00:e0:4c:68:06:ca"));
    }

    #[test]
    #[ignore = "needs Npcap; run with --include-ignored"]
    fn self_exclusion_with_no_adapters_matches_the_same_frames() {
        require_libpcap();

        let packets = one_of_each(frames::SELF_MAC);

        assert_eq!(
            matches(&all_excluding_self(&[]), &packets),
            matches(&all(), &packets)
        );
    }
}
