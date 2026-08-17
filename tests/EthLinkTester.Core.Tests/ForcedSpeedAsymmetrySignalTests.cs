using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class ForcedSpeedAsymmetrySignalTests
{
    private static NetworkAdapterInfo Adapter(
        string name, long bitsPerSecond, DuplexMode duplex = DuplexMode.Unknown) => new()
    {
        Id = $"{{{name}}}",
        Name = name,
        Description = $"{name} controller",
        MacAddress = "00-1B-21-11-22-33",
        Status = bitsPerSecond > 0 ? AdapterStatus.Up : AdapterStatus.Disconnected,
        LinkSpeedBitsPerSecond = bitsPerSecond,
        Duplex = bitsPerSecond > 0 ? duplex : DuplexMode.Unknown,
    };

    private const long Gigabit = 1_000_000_000;
    private const long Fast = 100_000_000;

    /// <summary>
    /// The ordinary healthy cycle: both ends were at a gigabit, one was forced down, the other
    /// followed. Everything else in this file is a way for that story to be false.
    /// </summary>
    private static ForcedSpeedProbe Cycle(
        long forcedAfter,
        long freeAfter,
        DuplexMode freeDuplex = DuplexMode.Half,
        long freeBefore = Gigabit,
        ConfigurationOutcome outcome = ConfigurationOutcome.Applied) => new()
    {
        ForcedBefore = Adapter("Ethernet", Gigabit, DuplexMode.Full),
        FreeBefore = Adapter("Ethernet 2", freeBefore, DuplexMode.Full),
        ForcedAfter = Adapter("Ethernet", forcedAfter, DuplexMode.Full),
        FreeAfter = Adapter("Ethernet 2", freeAfter, freeDuplex),
        Outcome = outcome,
    };

    /// <summary>
    /// The mechanism. One cable is one link, so an end that does not follow the other down is not
    /// connected to it - something is terminating each segment separately.
    /// </summary>
    [Fact]
    public void FreeEndStaysHigh_ProvesABridge()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Fast, freeAfter: Gigabit, freeDuplex: DuplexMode.Full));

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Conclusive, observation.Strength);
    }

    /// <summary>
    /// Following down at half duplex is the parallel-detection signature, and the strongest
    /// positive statement any signal in this set can make about a direct cable.
    /// </summary>
    /// <remarks>
    /// A forced PHY stops sending fast link pulses, so an auto-negotiating partner cannot
    /// negotiate; parallel detection brings the link up on the signalling alone, which carries
    /// speed but not duplex and defaults to half. Half at the far end therefore means the far end
    /// heard a silent PHY, which is what a cable delivers and a switch port does not.
    /// </remarks>
    [Fact]
    public void FreeEndFollowsDownHalfDuplex_ArguesDirect()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Fast, freeAfter: Fast, freeDuplex: DuplexMode.Half));

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
        Assert.Contains("parallel-detection", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The alias this signal could not previously see, now the other way up: a free end at
    /// 100 <b>full</b> negotiated its duplex with something, and the forced adapter had stopped
    /// negotiating - so the something is in the path.
    /// </summary>
    /// <remarks>
    /// This is the 100 Mbps switch the old comment described as indistinguishable and priced at
    /// Strong-Direct anyway. It is not indistinguishable; it was unread. Detecting it flips the
    /// finding rather than weakening it, which is the difference between a hardened signal and a
    /// hedged one.
    /// </remarks>
    [Fact]
    public void FreeEndFollowsDownFullDuplex_ProvesSomethingNegotiated()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Fast, freeAfter: Fast, freeDuplex: DuplexMode.Full));

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
    }

    /// <summary>Without a duplex reading the alias is back, so the finding cannot conclude.</summary>
    [Fact]
    public void FreeEndFollowsDownWithNoDuplexReported_OnlyCorroborates()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Fast, freeAfter: Fast, freeDuplex: DuplexMode.Unknown));

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
        Assert.Equal(SignalStrength.Suggestive, observation.Strength);
        Assert.False(
            TopologyVerdict.From([observation]).GradingIsAttributable,
            "an unread duplex leaves a 100 Mbps switch indistinguishable, which must not grade");
    }

    /// <summary>
    /// A link that goes dark after forcing says nothing about topology, in either direction. A
    /// direct pair whose far end cannot do 100 does exactly this - and so, more often, does a PHY
    /// that dropped automatic MDI/MDI-X when auto-negotiation went off.
    /// </summary>
    [Theory]
    [InlineData(0, Gigabit)]
    [InlineData(Fast, 0)]
    [InlineData(0, 0)]
    public void NoLinkAfterForcing_IsInconclusive(long forcedBits, long freeBits)
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: forcedBits, freeAfter: freeBits));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains("says nothing about topology", observation.Detail, StringComparison.Ordinal);
        Assert.Contains("MDI/MDI-X", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end: this signal alone is enough to conclude a direct link well enough to grade a
    /// cable, which nothing else in the set can do on its own.
    /// </summary>
    [Fact]
    public void FollowingDownAtHalfDuplex_PermitsGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            ForcedSpeedAsymmetrySignal.Observe(
                Cycle(forcedAfter: Fast, freeAfter: Fast, freeDuplex: DuplexMode.Half)),
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
                Cycle(forcedAfter: Fast, freeAfter: Gigabit, freeDuplex: DuplexMode.Full)),
        ]);

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.High, verdict.Confidence);
        Assert.False(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// The hazard the outcome exists for. A probe that died mid-cycle leaves the adapter pinned at
    /// 100; the next probe finds the value already correct, writes nothing, journals nothing - and
    /// both ends then read 100. Without it that is Direct at a grading strength, assembled from a
    /// state this probe did not create.
    /// </summary>
    [Fact]
    public void AForceThatWroteNothing_IsInconclusive()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(Cycle(
            forcedAfter: Fast,
            freeAfter: Fast,
            outcome: ConfigurationOutcome.AlreadyAtTarget));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains(
            "already fixed at a forced speed", observation.Detail, StringComparison.Ordinal);
        Assert.False(TopologyVerdict.From([observation]).GradingIsAttributable);
    }

    /// <summary>
    /// The dishonest driver, which this repo has already caught one adapter being: the
    /// <c>*SpeedDuplex</c> write is accepted and the PHY keeps negotiating, so both ends stay at a
    /// gigabit.
    /// </summary>
    /// <remarks>
    /// Read only after the force, this is <c>forcedSpeed == freeSpeed</c> and reported as a direct
    /// link - with a Detail sentence reading "was forced to 100 Mbps and Ethernet 2 followed it
    /// down to 1 Gbps", which states an observation nobody made and contradicts itself in the same
    /// clause. Checking that the forced end reached its target is what makes the sentence true.
    /// </remarks>
    [Fact]
    public void AForceTheDriverIgnored_IsInconclusive()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Gigabit, freeAfter: Gigabit, freeDuplex: DuplexMode.Full));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains("without honouring it", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path that was already at 100 before the test: a 10/100 device in line, a switch port
    /// pinned to 100, or a cable that will not negotiate higher. Both ends read 100 afterwards and
    /// nothing moved, so there is no transition to have witnessed.
    /// </summary>
    /// <remarks>
    /// This is the case the before-readings exist for, and the one that fooled the old signal in
    /// company: a repeater-class box that passes everything defeats the multicast sweep at the same
    /// time, so both Direct-capable signals used to fail together and agree.
    /// </remarks>
    [Fact]
    public void APathAlreadyAtTheTarget_IsInconclusive()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(Cycle(
            forcedAfter: Fast,
            freeAfter: Fast,
            freeDuplex: DuplexMode.Half,
            freeBefore: Fast));

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.Contains("no drop for it to follow", observation.Detail, StringComparison.Ordinal);
        Assert.False(TopologyVerdict.From([observation]).GradingIsAttributable);
    }

    /// <summary>Every Detail names the transition it saw, so a report can be argued with.</summary>
    [Fact]
    public void TheDetailNamesBothEndsOfTheTransition()
    {
        var observation = ForcedSpeedAsymmetrySignal.Observe(
            Cycle(forcedAfter: Fast, freeAfter: Fast, freeDuplex: DuplexMode.Half));

        Assert.Contains(
            LinkSpeed.Mbps1000.ShortName(), observation.Detail, StringComparison.Ordinal);
        Assert.Contains(
            LinkSpeed.Mbps100.ShortName(), observation.Detail, StringComparison.Ordinal);
    }
}
