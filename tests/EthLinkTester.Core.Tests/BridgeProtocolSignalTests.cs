using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class BridgeProtocolSignalTests
{
    /// <summary>
    /// Hearing a management protocol proves a device, because a cable does not announce itself.
    /// </summary>
    [Fact]
    public void HearingLldp_MeansSomethingIsThere()
    {
        var observation = BridgeProtocolSignal.Observe(
            new PassiveListenResult(TimeSpan.FromSeconds(200), Lldp: 3, Stp: 0, Cdp: 0));

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
        Assert.Contains("LLDP", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>The protocols heard are named, because "something was heard" is not a finding.</summary>
    [Fact]
    public void TheDetailNamesEveryProtocolHeard()
    {
        var observation = BridgeProtocolSignal.Observe(
            new PassiveListenResult(TimeSpan.FromSeconds(200), Lldp: 1, Stp: 0, Cdp: 2));

        Assert.Contains("LLDP", observation.Detail, StringComparison.Ordinal);
        Assert.Contains("CDP", observation.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("STP", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Silence after a full window settles nothing, and must never read as a direct cable.
    /// </summary>
    /// <remarks>
    /// The single easiest place in this model to turn absence of evidence into evidence of absence.
    /// An unmanaged switch has no management plane at all - the reference GS308 emits nothing
    /// whatsoever - so reading silence as "no bridge" would produce a confident direct verdict on
    /// every path built from an unmanaged switch, which is most switched paths.
    /// </remarks>
    [Fact]
    public void SilenceAfterAFullWindow_SettlesNothing()
    {
        var observation = BridgeProtocolSignal.Observe(
            PassiveListenResult.Silent(BridgeProtocolSignal.HonestSilence));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.NotEqual(TopologyFinding.Direct, observation.Finding);
        Assert.Contains("settles nothing", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And silence after a short window says so, rather than passing itself off as the same thing.
    /// </summary>
    [Fact]
    public void SilenceAfterAShortWindow_SaysItWasTooShort()
    {
        var observation = BridgeProtocolSignal.Observe(
            PassiveListenResult.Silent(TimeSpan.FromSeconds(5)));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains("about the length of the listen", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window has to outlast three CDP intervals, asserted here as the engine asserts it at
    /// compile time.
    /// </summary>
    /// <remarks>
    /// CDP announces every 60 s with a 180 s hold, LLDP every 30 s, STP hello every 2 s. Shortening
    /// this below 180 makes "heard nothing" a statement about patience rather than about the
    /// segment. The engine's <c>passive::HONEST_SILENCE_SECONDS</c> carries the same floor as a
    /// <c>const</c> assertion; this is the managed half of the same claim.
    /// </remarks>
    [Fact]
    public void TheHonestWindowOutlastsThreeCdpIntervals() =>
        Assert.True(
            BridgeProtocolSignal.HonestSilence >= TimeSpan.FromSeconds(180),
            "silence shorter than three CDP intervals is impatience, not evidence");

    /// <summary>Nothing this signal produces can ever conclude a direct link.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ThisSignalNeverArguesForACable(int lldp)
    {
        var observation = BridgeProtocolSignal.Observe(
            new PassiveListenResult(BridgeProtocolSignal.HonestSilence, lldp, 0, 0));

        Assert.NotEqual(TopologyFinding.Direct, observation.Finding);
    }
}
