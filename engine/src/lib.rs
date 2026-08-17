//! Raw Layer 2 traffic engine.
//!
//! Exists because sockets cannot test a cable between two NICs in one machine: Windows' TCP/IP
//! stack recognises both addresses as local and short-circuits the traffic through loopback, so
//! the frames never reach a PHY. Npcap installs as an NDIS lightweight filter *below* the stack,
//! where there is no routing decision left to short-circuit.
//!
//! That is not a theory here - `tools/Verify-Wire.ps1` establishes it against the reference rig by
//! bracketing a `bin/wirecheck.rs` run with both NICs' own hardware counters, and its result is
//! recorded in the repository so a regression is visible rather than assumed. The counters are
//! what carry the claim: `wirecheck` counts frames in userspace, and userspace cannot tell a frame
//! that crossed a cable from one a bridge handed back.

// Windows by construction, not by omission. Npcap's NDIS filter, the adapter counters and the
// managed host are all Windows-only, and `diag` is `#[cfg(windows)]` throughout - so a build
// elsewhere used to fail with "cannot find function `require_npcap`", which reads like a missing
// import rather than a missing platform. A stub would be worse than this message: it would compile
// and then not put frames on any wire.
#[cfg(not(windows))]
compile_error!(
    "ethlink-engine targets Windows - it injects frames through Npcap's NDIS lightweight filter, \
     which has no counterpart on this platform."
);

pub mod diag;
pub mod engine;
pub mod ffi;
pub mod frame;
pub mod histogram;
pub mod passive;
pub mod ring;
pub mod topology;

pub use engine::{Engine, EngineFault, RunConfig, StartError};

/// Bytes each frame occupies on the wire beyond the buffer handed to pcap.
///
/// Seven bytes of preamble, one start-of-frame delimiter, four of FCS the NIC appends, and the
/// twelve-byte interframe gap the standard requires before the next frame may start. Throughput is
/// reported against this figure, because "percentage of line rate" is only meaningful if the
/// overhead the line rate includes is counted too - at 64-byte frames it is more than a quarter of
/// the traffic, and omitting it understates a saturated link as 73% busy.
///
/// The managed simulator must use the same definition. It counts from the 1518-byte wire frame
/// (which already contains the FCS) and so adds 20; this counts from the 1514-byte buffer and adds
/// 24. Both arrive at 1538, and a review found them 29% apart at 64 bytes before they did.
pub const WIRE_OVERHEAD_BYTES: usize = 24;

/// The 1538 both sides have to reach, asserted here and again in `EthernetFrameTests`.
///
/// The two constants differ by four on purpose, because they count from different starting points,
/// and that is exactly the shape of agreement that decays into a wrong number: the prose above
/// explained the relationship and nothing checked it. This is the arithmetic, on the Rust side of
/// the seam; the managed side asserts `WireBytes(1518) == 1538` against the same total. Compile-time
/// rather than a test, since both operands are constants and a change should fail the build.
const _: () = assert!(
    1514 + WIRE_OVERHEAD_BYTES == 1538,
    "a maximum-size frame occupies 1538 bytes of wire time; the managed side counts to the same \
     total from the 1518-byte wire frame"
);

/// Telemetry as the managed host reads it.
///
/// Must stay byte-identical to `EthLinkTester.Core.Engine.TelemetrySample`, which pins the same
/// layout with `StructLayout(LayoutKind.Sequential, Pack = 8)` and asserts every field offset in
/// `TelemetrySampleLayoutTests`. The host reads these straight out of shared memory through a span
/// over a raw pointer, so nothing checks agreement at run time and nothing would fail to compile
/// if the two drifted - the engine would simply write garbage and the app would plot it.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TelemetrySample {
    pub timestamp_ticks: i64,
    pub tx_megabits_per_second: f64,
    pub rx_megabits_per_second: f64,
    pub latency_p50_microseconds: f64,
    pub latency_p99_microseconds: f64,
    pub tx_frames: i64,
    pub rx_frames: i64,
    /// Frames the kernel matched and the capture buffer then lost. Not errors, and not cable loss.
    ///
    /// This field was `rx_errors` at the ABI - the one boundary a new engine-side reader inspects
    /// first - long after both the managed property and the engine's own counter had been renamed
    /// away from that word for being actively misleading. A name is not layout, so nothing broke;
    /// it simply told everyone arriving at the seam the wrong thing about what the number means.
    pub rx_capture_drops: i64,
}

/// The EtherType every frame this engine generates carries.
///
/// IEEE local experimental ethertype 1. Chosen so the engine can count *its own* frames rather
/// than reading adapter-wide counters, which include the OS background traffic that would
/// otherwise mask real loss.
pub const PROBE_ETHERTYPE: u16 = 0x88B5;

/// Marks a frame as ours beyond the EtherType alone.
pub const PROBE_MAGIC: &[u8; 8] = b"ELTPROBE";

#[cfg(test)]
mod tests {
    use super::*;

    /// The managed side pins this at 64 bytes with fields at 0,8,16,24,32,40,48,56. If this fails,
    /// the FFI contract is broken and the host will read misaligned garbage.
    ///
    /// Every field's offset is asserted, not just the total size. Swapping two same-width fields -
    /// transmit and receive throughput, say - keeps the struct exactly 64 bytes and every
    /// alignment intact, so a size check passes while the app plots each direction's rate under
    /// the other's name. Nothing would fail to compile and the numbers would look entirely
    /// plausible.
    #[test]
    fn telemetry_sample_matches_the_managed_layout() {
        use core::mem::offset_of;

        assert_eq!(core::mem::size_of::<TelemetrySample>(), 64);
        assert_eq!(core::mem::align_of::<TelemetrySample>(), 8);

        assert_eq!(offset_of!(TelemetrySample, timestamp_ticks), 0);
        assert_eq!(offset_of!(TelemetrySample, tx_megabits_per_second), 8);
        assert_eq!(offset_of!(TelemetrySample, rx_megabits_per_second), 16);
        assert_eq!(offset_of!(TelemetrySample, latency_p50_microseconds), 24);
        assert_eq!(offset_of!(TelemetrySample, latency_p99_microseconds), 32);
        assert_eq!(offset_of!(TelemetrySample, tx_frames), 40);
        assert_eq!(offset_of!(TelemetrySample, rx_frames), 48);
        assert_eq!(offset_of!(TelemetrySample, rx_capture_drops), 56);
    }
}
