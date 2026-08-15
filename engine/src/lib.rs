//! Raw Layer 2 traffic engine.
//!
//! Exists because sockets cannot test a cable between two NICs in one machine: Windows' TCP/IP
//! stack recognises both addresses as local and short-circuits the traffic through loopback, so
//! the frames never reach a PHY. Npcap installs as an NDIS lightweight filter *below* the stack,
//! where there is no routing decision left to short-circuit.
//!
//! That is not a theory here - `bin/wirecheck.rs` proves it against the reference rig, and its
//! result is recorded in the repository so a regression is visible rather than assumed.

pub mod engine;
pub mod ffi;
pub mod frame;
pub mod histogram;
pub mod ring;

pub use engine::{Engine, RunConfig};

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
    #[test]
    fn telemetry_sample_matches_the_managed_layout() {
        assert_eq!(core::mem::size_of::<TelemetrySample>(), 64);
        assert_eq!(core::mem::align_of::<TelemetrySample>(), 8);
    }
}
