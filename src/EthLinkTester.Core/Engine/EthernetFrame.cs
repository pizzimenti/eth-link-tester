namespace EthLinkTester.Core.Engine;

/// <summary>
/// What a frame costs on the wire, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations of <see cref="IPacketEngine"/> each did this arithmetic for themselves and
/// disagreed by up to 29% at 64-byte frames: the native engine reported the bytes it handed to
/// pcap, the simulator added preamble and interframe gap. Both are defensible in isolation and
/// having both means the same cable grades differently depending on which engine measured it.
/// </para>
/// <para>
/// The overhead is counted, because "percentage of line rate" is only meaningful against the
/// figure line rate actually includes. At 1518-byte frames it is 1.3% and arguable; at 64 bytes it
/// is 24% of the traffic, and omitting it renders a fully saturated gigabit link as 761 Mbps.
/// </para>
/// <para>
/// The engine has the same constant expressed from the other end - it starts from the 1514-byte
/// buffer handed to pcap and adds 24, where this starts from the 1518-byte wire frame and adds 20.
/// Both reach 1538. See <c>WIRE_OVERHEAD_BYTES</c> in <c>engine/src/lib.rs</c>.
/// </para>
/// </remarks>
public static class EthernetFrame
{
    /// <summary>Smallest legal frame including the FCS.</summary>
    public const int MinimumBytes = 64;

    /// <summary>Largest standard frame including the FCS. Jumbo frames exceed this by agreement.</summary>
    public const int MaximumBytes = 1518;

    /// <summary>
    /// Bytes on the wire beyond the frame itself: a 7-byte preamble, the start-of-frame delimiter,
    /// and the 12-byte interframe gap the standard requires before the next frame may begin.
    /// </summary>
    public const int WireOverheadBytes = 20;

    /// <summary>Total wire time cost of one frame, in bytes.</summary>
    /// <param name="frameBytes">Frame size including the FCS, as RFC 2544 quotes it.</param>
    public static int WireBytes(int frameBytes) => frameBytes + WireOverheadBytes;

    /// <summary>Frames per second a link of <paramref name="bitsPerSecond"/> can carry.</summary>
    public static double FramesPerSecond(long bitsPerSecond, int frameBytes) =>
        bitsPerSecond / (double)(WireBytes(frameBytes) * 8);
}
