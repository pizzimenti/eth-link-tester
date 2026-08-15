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
/// Which run produced this frame. See [`RUN_ID_OFFSET`].
pub const RUN_ID: std::ops::Range<usize> = 34..36;

/// Where [`RUN_ID`] starts, for the kernel filter.
///
/// The filter is written as a byte offset rather than reusing the range, because BPF has no notion
/// of our field names and the two must agree exactly. A constant both sides read is the only way
/// that agreement survives someone inserting a field above it.
pub const RUN_ID_OFFSET: usize = RUN_ID.start;

/// Everything before the padding. A 64-byte wire frame leaves 60 bytes of buffer, so the header
/// fits with room to spare.
pub const HEADER_LEN: usize = RUN_ID.end;

/// Smallest buffer pcap will accept for a legal frame: 64 on the wire, less the 4-byte FCS the
/// NIC appends.
pub const MIN_BUFFER: usize = 60;

/// Largest buffer accepted: a 9018-byte jumbo frame less the FCS.
///
/// Bounded because pcap rejects an oversize frame per send, and the transmit loop treats a rejected
/// send as a fatal fault. Without this a caller asking for a frame the link cannot carry gets a run
/// that dies on its first batch, which is a confusing way to be told the frame size was wrong.
pub const MAX_BUFFER: usize = 9014;

pub fn build(dst: [u8; 6], src: [u8; 6], len: usize, run_id: u16) -> Vec<u8> {
    let len = len.clamp(MIN_BUFFER, MAX_BUFFER);
    let mut frame = vec![0u8; len];
    frame[DST].copy_from_slice(&dst);
    frame[SRC].copy_from_slice(&src);
    frame[ETHERTYPE].copy_from_slice(&PROBE_ETHERTYPE.to_be_bytes());
    frame[MAGIC].copy_from_slice(PROBE_MAGIC);
    frame[RUN_ID].copy_from_slice(&run_id.to_be_bytes());
    frame
}

/// A send time of zero means "do not time this frame". See [`stamp`].
pub const UNTIMED: u64 = 0;

/// Stamps a prepared frame in place. Kept separate from `build` because the hot path reuses one
/// buffer - allocating per frame measured the allocator rather than the wire.
///
/// `sent_nanos` may be [`UNTIMED`], and for most frames it is. A frame is stamped when it is
/// *queued*, so frame *k* of a batch carries the time it waited for the k-1 frames ahead of it to
/// serialise - which is a measurement of the batch, not of the link. Only the frame that leaves
/// first has a delay attributable to the hardware, so only that one is timed, and the rest carry
/// zero rather than a figure that would be dominated by their queue position.
pub fn stamp(frame: &mut [u8], seq: u32, sent_nanos: u64) {
    frame[SEQ].copy_from_slice(&seq.to_be_bytes());
    frame[SENT_NANOS].copy_from_slice(&sent_nanos.to_be_bytes());
}

/// Reads a captured frame's sequence and send time, or None when it is not this run's.
///
/// The send time is [`UNTIMED`] for every frame the sender did not time; the caller must skip
/// those rather than treating them as zero-latency arrivals.
///
/// Both the ethertype and the run id are also matched in the kernel, so this is a second check
/// rather than the only one - cheap insurance against another application choosing the same
/// experimental ethertype, and against a stale frame from a previous run still in a buffer.
pub fn parse(data: &[u8], run_id: u16) -> Option<(u32, u64)> {
    if data.len() < HEADER_LEN
        || &data[MAGIC] != PROBE_MAGIC
        || data[RUN_ID] != run_id.to_be_bytes()
    {
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
        let mut frame = build([1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11, 12], MIN_BUFFER, 7);
        stamp(&mut frame, 4242, 1_234_567_890);

        assert_eq!(parse(&frame, 7), Some((4242, 1_234_567_890)));
    }

    #[test]
    fn a_short_buffer_is_padded_to_a_legal_frame() {
        assert_eq!(build([0; 6], [0; 6], 10, 1).len(), MIN_BUFFER);
    }

    #[test]
    fn an_oversize_buffer_is_capped_at_a_jumbo_frame() {
        assert_eq!(build([0; 6], [0; 6], 65_535, 1).len(), MAX_BUFFER);
    }

    /// Someone else's traffic on the same experimental ethertype must not be counted as ours, or
    /// it would inflate the receive count and hide real loss.
    #[test]
    fn a_frame_without_the_magic_is_not_ours() {
        let mut frame = build([0; 6], [0; 6], MIN_BUFFER, 1);
        frame[MAGIC][0] = b'X';

        assert_eq!(parse(&frame, 1), None);
    }

    /// Two engines on one rig - the obvious way to test a link in both directions at once - would
    /// otherwise each count the other's frames as delivered, and a cable losing every frame in one
    /// direction would still report a full receive count.
    #[test]
    fn another_runs_frame_is_not_ours() {
        let frame = build([0; 6], [0; 6], MIN_BUFFER, 1);

        assert_eq!(parse(&frame, 2), None);
        assert!(parse(&frame, 1).is_some());
    }

    #[test]
    fn a_runt_is_rejected_rather_than_indexed_out_of_bounds() {
        assert_eq!(parse(&[0u8; 20], 1), None);
    }

    /// The BPF filter addresses this field by number. If the header grows above it and this is not
    /// updated in step, the kernel silently matches the wrong two bytes and the receive count
    /// becomes whatever happened to be there.
    #[test]
    fn the_filter_offset_matches_the_field() {
        let frame = build([0; 6], [0; 6], MIN_BUFFER, 0xBEEF);

        assert_eq!(
            u16::from_be_bytes(frame[RUN_ID_OFFSET..RUN_ID_OFFSET + 2].try_into().unwrap()),
            0xBEEF
        );
    }
}
