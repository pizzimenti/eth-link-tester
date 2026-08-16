using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class ForcedSpeedAsymmetrySignalTests
{
    private static NetworkAdapterInfo Adapter(string name, long bitsPerSecond) => new()
    {
        Id = $"{{{name}}}",
        Name = name,
        Description = $"{name} controller",
        MacAddress = "00-1B-21-11-22-33",
        Status = bitsPerSecond > 0 ? AdapterStatus.Up : AdapterStatus.Disconnected,
        LinkSpeedBitsPerSecond = bitsPerSecond,
    };

    /// <summary>
    /// The mechanism. One cable is one link, so an end that does not follow the other down is not
    /// connected to it - something is terminating each segment separately.
    /// </summary>
    [Fact]
    public void FreeEndStaysHigh_ProvesABridge()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Adapter("Ethernet", 100_000_000),
            Adapter("Ethernet 2", 1_000_000_000),
            forceWasApplied: true);

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Conclusive, observation.Strength);
    }

    /// <summary>
    /// Following down is what a cable does - and also what a 100 Mbps switch does, so this is
    /// Strong rather than Conclusive. It is still the only signal in the set that can positively
    /// argue for a direct link at that strength.
    /// </summary>
    [Fact]
    public void FreeEndFollowsDown_ArguesDirect_ButNotConclusively()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Adapter("Ethernet", 100_000_000),
            Adapter("Ethernet 2", 100_000_000),
            forceWasApplied: true);

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
        Assert.NotEqual(SignalStrength.Conclusive, observation.Strength);
    }

    /// <summary>
    /// A link that goes dark after forcing says nothing about topology, in either direction. A
    /// direct pair whose far end cannot do 100 does exactly this.
    /// </summary>
    [Theory]
    [InlineData(0, 1_000_000_000)]
    [InlineData(100_000_000, 0)]
    [InlineData(0, 0)]
    public void NoLinkAfterForcing_IsInconclusive(long forcedBits, long freeBits)
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Adapter("Ethernet", forcedBits),
            Adapter("Ethernet 2", freeBits),
            forceWasApplied: true);

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains("says nothing about topology", observation.Detail);
    }

    /// <summary>
    /// End to end: this signal alone is enough to conclude a direct link well enough to grade a
    /// cable, which nothing else in the set can do on its own.
    /// </summary>
    [Fact]
    public void FollowingDown_PermitsGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            ForcedSpeedAsymmetrySignal.Observe(
                Adapter("Ethernet", 100_000_000),
                Adapter("Ethernet 2", 100_000_000),
                forceWasApplied: true),
        ]);

        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
        Assert.True(verdict.GradingIsAttributable);
    }

    /// <summary>And a bridge found this way still blocks grading, at high confidence.</summary>
    [Fact]
    public void StayingHigh_BlocksGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            ForcedSpeedAsymmetrySignal.Observe(
                Adapter("Ethernet", 100_000_000),
                Adapter("Ethernet 2", 1_000_000_000),
                forceWasApplied: true),
        ]);

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.High, verdict.Confidence);
        Assert.False(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// The hazard the flag exists for. A probe that died mid-cycle leaves the adapter pinned at
    /// 100; the next probe finds the value already correct, writes nothing, journals nothing - and
    /// both ends then read 100. Without the flag that is Direct at Strong strength, assembled from
    /// a state this probe did not create.
    /// </summary>
    [Fact]
    public void MatchingSpeedsWithoutHavingForcedAnything_IsInconclusive()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Adapter("Ethernet", 100_000_000),
            Adapter("Ethernet 2", 100_000_000),
            forceWasApplied: false);

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.NotEqual(TopologyFinding.Direct, observation.Finding);
        Assert.Contains("already fixed at a forced speed", observation.Detail);
    }

    /// <summary>And it cannot license grading either, which is the consequence that matters.</summary>
    [Fact]
    public void AnUnappliedForce_CannotLicenseGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            ForcedSpeedAsymmetrySignal.Observe(
                Adapter("Ethernet", 100_000_000),
                Adapter("Ethernet 2", 100_000_000),
                forceWasApplied: false),
        ]);

        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
        Assert.False(verdict.GradingIsAttributable);
    }
}
