using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class LinkSpeedSignalTests
{
    private static NetworkAdapterInfo Adapter(string name, long bitsPerSecond) => new()
    {
        Id = $"{{{name}}}",
        Name = name,
        Description = $"{name} controller",
        MacAddress = "00-1B-21-11-22-33",
        Status = AdapterStatus.Up,
        LinkSpeedBitsPerSecond = bitsPerSecond,
    };

    /// <summary>
    /// The signal's whole value. Auto-negotiation is a conversation between exactly two PHYs, so
    /// one cable resolves to one speed at both ends - two different speeds cannot be one cable.
    /// </summary>
    [Fact]
    public void DifferentSpeeds_ProveABridge()
    {
        var observation = LinkSpeedSignal.Observe(
            Adapter("Ethernet", 1_000_000_000),
            Adapter("Ethernet 2", 100_000_000));

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Conclusive, observation.Strength);
        Assert.Contains("1 Gbps", observation.Detail);
        Assert.Contains("100 Mbps", observation.Detail);
    }

    /// <summary>
    /// The trap. Two ports at the same speed is exactly what a direct cable looks like and exactly
    /// what a switch of that speed looks like, so agreement must never read as Direct - that would
    /// be the easiest possible route to a confident wrong answer.
    /// </summary>
    [Fact]
    public void MatchingSpeeds_ProveNothing()
    {
        var observation = LinkSpeedSignal.Observe(
            Adapter("Ethernet", 1_000_000_000),
            Adapter("Ethernet 2", 1_000_000_000));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.NotEqual(TopologyFinding.Direct, observation.Finding);
    }

    /// <summary>
    /// A port with no negotiated speed is neither agreement nor disagreement. Treating an
    /// unplugged or still-negotiating port as a match would let a dead link read as a healthy
    /// direct one.
    /// </summary>
    [Theory]
    [InlineData(0, 1_000_000_000)]
    [InlineData(1_000_000_000, 0)]
    [InlineData(0, 0)]
    public void AnUnreportedSpeed_IsInconclusive(long transmitBits, long receiveBits)
    {
        var observation = LinkSpeedSignal.Observe(
            Adapter("Ethernet", transmitBits),
            Adapter("Ethernet 2", receiveBits));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
    }

    /// <summary>A mismatch alone is enough for the verdict, and at high confidence.</summary>
    [Fact]
    public void AMismatch_CarriesTheWholeVerdict()
    {
        var verdict = TopologyVerdict.From(
        [
            LinkSpeedSignal.Observe(
                Adapter("Ethernet", 2_500_000_000),
                Adapter("Ethernet 2", 1_000_000_000)),
        ]);

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.High, verdict.Confidence);
        Assert.False(verdict.GradingIsAttributable);
    }
}
