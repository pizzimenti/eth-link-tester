//! Raw Layer 2 traffic engine.
//!
//! Exists because sockets cannot test a cable between two NICs in one machine: Windows' TCP/IP
//! stack recognises both addresses as local and short-circuits the traffic through loopback, so
//! the frames never reach a PHY. Npcap installs as an NDIS lightweight filter *below* the stack,
//! where there is no routing decision left to short-circuit.
//!
//! That is not a theory here - `bin/wirecheck.rs` proves it against the reference rig, and its
//! result is recorded in the repository so a regression is visible rather than assumed.

pub mod diag;
pub mod engine;
pub mod ffi;
pub mod frame;
pub mod histogram;
pub mod ring;

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
    pub rx_errors: i64,
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
        assert_eq!(core::mem::size_of::<TelemetrySample>(), 64);
        assert_eq!(core::mem::align_of::<TelemetrySample>(), 8);

        let sample = TelemetrySample::default();
        let base = &sample as *const _ as usize;
        let offset = |field: *const _| field as usize - base;

        assert_eq!(offset(&sample.timestamp_ticks as *const _ as *const u8), 0);
        assert_eq!(
            offset(&sample.tx_megabits_per_second as *const _ as *const u8),
            8
        );
        assert_eq!(
            offset(&sample.rx_megabits_per_second as *const _ as *const u8),
            16
        );
        assert_eq!(
            offset(&sample.latency_p50_microseconds as *const _ as *const u8),
            24
        );
        assert_eq!(
            offset(&sample.latency_p99_microseconds as *const _ as *const u8),
            32
        );
        assert_eq!(offset(&sample.tx_frames as *const _ as *const u8), 40);
        assert_eq!(offset(&sample.rx_frames as *const _ as *const u8), 48);
        assert_eq!(offset(&sample.rx_errors as *const _ as *const u8), 56);
    }
}
