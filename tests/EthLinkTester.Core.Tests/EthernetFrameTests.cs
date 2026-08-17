using EthLinkTester.Core.Engine;

namespace EthLinkTester.Core.Tests;

/// <summary>
/// Pins the wire-overhead arithmetic against the engine's copy of it.
/// </summary>
/// <remarks>
/// The two constants differ by four on purpose - the engine counts from the 1514-byte buffer it
/// hands to pcap and adds 24, this counts from the 1518-byte wire frame and adds 20 - and both have
/// to arrive at 1538. That relationship was explained in prose on both sides and asserted on
/// neither, which is precisely how the same seam produced two answers 29% apart at 64 bytes the
/// first time. The engine now has a compile-time assertion of the same total.
/// </remarks>
public class EthernetFrameTests
{
    /// <summary>The number both sides have to reach.</summary>
    [Fact]
    public void AMaximumFrameOccupies1538BytesOfWireTime() =>
        Assert.Equal(1538, EthernetFrame.WireBytes(EthernetFrame.MaximumBytes));

    /// <summary>And the minimum, where getting this wrong costs 24% rather than 1.3%.</summary>
    [Fact]
    public void AMinimumFrameOccupies84BytesOfWireTime() =>
        Assert.Equal(84, EthernetFrame.WireBytes(EthernetFrame.MinimumBytes));

    /// <summary>
    /// The line rates the whole throughput figure is quoted against: 1,488,095 frames a second at
    /// 64 bytes on gigabit, and 81,274 at 1518.
    /// </summary>
    /// <remarks>
    /// These are the published 802.3 numbers, and they are the reason the overhead is counted at
    /// all. Omitting it renders a fully saturated gigabit link at 64-byte frames as 761 Mbps.
    /// </remarks>
    [Theory]
    [InlineData(64, 1_488_095)]
    [InlineData(1518, 81_274)]
    public void FramesPerSecondMatchesTheStandardsFigure(int frameBytes, int expected) =>
        Assert.Equal(
            expected, (int)EthernetFrame.FramesPerSecond(1_000_000_000, frameBytes));
}
