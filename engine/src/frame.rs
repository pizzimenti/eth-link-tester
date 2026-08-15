//! The probe frame: what this engine puts on the wire and recognises coming back.
//!
//! Carries its own identity and its own send time. Identity is what lets the engine count *its*
//! frames rather than reading adapter-wide counters that include the operating system's background
//! chatter - which would inflate receives and mask real loss. The send time is what makes one-way
//! latency measurable at all.

use crate::{PROBE_ETHERTYPE, PROBE_MAGIC};

pub const DST: std::ops::Range<usize> = 0..6;
pub const SRC: std::ops::Range<usize> = 6..12;
pub const ETHERTYPE: std::ops::Range<usize> = 12..14;
pub const MAGIC: std::ops::Range<usize> = 14..22;
pub const SEQ: std::ops::Range<usize> = 22..26;
pub const SENT_NANOS: std::ops::Range<usize> = 26..34;

/// Everything before the padding. A 64-byte wire frame leaves 60 bytes of buffer, so the header
/// fits with room to spare.
pub const HEADER_LEN: usize = 34;

/// Smallest buffer pcap will accept for a legal frame: 64 on the wire, less the 4-byte FCS the
/// NIC appends.
pub const MIN_BUFFER: usize = 60;

pub fn build(dst: [u8; 6], src: [u8; 6], len: usize) -> Vec<u8> {
    let len = len.max(MIN_BUFFER);
    let mut frame = vec![0u8; len];
    frame[DST].copy_from_slice(&dst);
    frame[SRC].copy_from_slice(&src);
    frame[ETHERTYPE].copy_from_slice(&PROBE_ETHERTYPE.to_be_bytes());
    frame[MAGIC].copy_from_slice(PROBE_MAGIC);
    frame
}

/// Stamps a prepared frame in place. Kept separate from `build` because the hot path reuses one
/// buffer - allocating per frame measured the allocator rather than the wire.
pub fn stamp(frame: &mut [u8], seq: u32, sent_nanos: u64) {
    frame[SEQ].copy_from_slice(&seq.to_be_bytes());
    frame[SENT_NANOS].copy_from_slice(&sent_nanos.to_be_bytes());
}

/// Reads a captured frame's sequence and send time, or None when it is not one of ours.
///
/// The ethertype filter runs in the kernel, so this is a second check rather than the only one -
/// cheap insurance against another application choosing the same experimental ethertype.
pub fn parse(data: &[u8]) -> Option<(u32, u64)> {
    if data.len() < HEADER_LEN || &data[MAGIC] != PROBE_MAGIC {
        return None;
    }

    let seq = u32::from_be_bytes(data[SEQ].try_into().ok()?);
    let sent = u64::from_be_bytes(data[SENT_NANOS].try_into().ok()?);
    Some((seq, sent))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_sequence_and_timestamp() {
        let mut frame = build([1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11, 12], MIN_BUFFER);
        stamp(&mut frame, 4242, 1_234_567_890);

        assert_eq!(parse(&frame), Some((4242, 1_234_567_890)));
    }

    #[test]
    fn a_short_buffer_is_padded_to_a_legal_frame() {
        assert_eq!(build([0; 6], [0; 6], 10).len(), MIN_BUFFER);
    }

    /// Someone else's traffic on the same experimental ethertype must not be counted as ours, or
    /// it would inflate the receive count and hide real loss.
    #[test]
    fn a_frame_without_the_magic_is_not_ours() {
        let mut frame = build([0; 6], [0; 6], MIN_BUFFER);
        frame[MAGIC][0] = b'X';

        assert_eq!(parse(&frame), None);
    }

    #[test]
    fn a_runt_is_rejected_rather_than_indexed_out_of_bounds() {
        assert_eq!(parse(&[0u8; 20]), None);
    }
}
