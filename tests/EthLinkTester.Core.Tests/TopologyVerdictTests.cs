using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

/// <summary>
/// Covers the rule that makes this model worth having: the two wrong answers do not cost the same.
/// </summary>
/// <remarks>
/// Calling a direct cable bridged wastes an afternoon. Calling a bridged path direct makes every
/// later result a lie - the run measures two cables and a switch, and Phase 6 grades all of it as
/// one cable against IEEE limits, with a confidence interval attached. Nothing downstream can
/// detect that, so it has to be impossible here.
/// </remarks>
public class TopologyVerdictTests
{
    private static TopologyObservation Says(
        TopologySignal signal, TopologyFinding finding, SignalStrength strength) =>
        new(signal, finding, strength, $"{signal} says {finding} ({strength})");

    [Fact]
    public void NoObservations_IsUnknown_NotDirect()
    {
        var verdict = TopologyVerdict.From([]);

        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.None, verdict.Confidence);
        Assert.False(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// Every signal reporting Inconclusive is the ordinary case on an unmanaged switch, which may
    /// emit nothing at all. It must not read as a direct cable.
    /// </summary>
    [Fact]
    public void AllSignalsSilent_IsUnknown_NotDirect()
    {
        var verdict = TopologyVerdict.From(
        [
            TopologyObservation.Nothing(TopologySignal.BridgeProtocolTraffic, "no LLDP, CDP or STP seen"),
            TopologyObservation.Nothing(TopologySignal.LinkSpeedMismatch, "both ports at 1 Gbps"),
            TopologyObservation.Nothing(TopologySignal.LatencySlope, "slope within noise"),
        ]);

        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
        Assert.False(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// A suggestive signal is not enough on its own to claim a direct link, because none of the
    /// suggestive signals can actually see a cable - they can only fail to see a bridge.
    /// </summary>
    [Fact]
    public void SuggestiveDirectAlone_DoesNotConcludeDirect()
    {
        var verdict = TopologyVerdict.From(
        [
            Says(TopologySignal.LatencySlope, TopologyFinding.Direct, SignalStrength.Suggestive),
        ]);

        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
        Assert.False(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// The one place Direct is reachable, named deliberately.
    /// </summary>
    /// <remarks>
    /// <see cref="TopologySignal.ForcedSpeedAsymmetry"/> and not
    /// <see cref="TopologySignal.ReservedMulticastProbe"/>, which is what this test used to say. The
    /// probe's crossed branch cannot produce Strong any more, so naming it here asserted a
    /// combination the system cannot construct - and pinned the belief that a sweep alone may grade
    /// a cable, which is the belief two other tests' docstrings deny.
    /// </remarks>
    [Fact]
    public void StrongDirect_ConcludesDirect_AndPermitsGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            Says(TopologySignal.ForcedSpeedAsymmetry, TopologyFinding.Direct, SignalStrength.Strong),
        ]);

        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.Moderate, verdict.Confidence);
        Assert.True(verdict.GradingIsAttributable);
    }

    /// <summary>
    /// The asymmetry, stated directly: one suggestive sign of a bridge beats a strong sign of a
    /// direct link. The signals that can see a bridge are the same ones that fall silent when they
    /// cannot, so a positive from any of them outranks the others failing to find one.
    /// </summary>
    [Fact]
    public void AnyBridgedSignal_OutranksDirectOnes()
    {
        var verdict = TopologyVerdict.From(
        [
            Says(TopologySignal.ForcedSpeedAsymmetry, TopologyFinding.Direct, SignalStrength.Strong),
            Says(TopologySignal.BridgeProtocolTraffic, TopologyFinding.Bridged, SignalStrength.Suggestive),
        ]);

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.False(verdict.GradingIsAttributable);
    }

    [Fact]
    public void ConclusiveBridged_IsHighConfidence()
    {
        var verdict = TopologyVerdict.From(
        [
            Says(TopologySignal.LinkSpeedMismatch, TopologyFinding.Bridged, SignalStrength.Conclusive),
        ]);

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.High, verdict.Confidence);
    }

    /// <summary>Two agreeing suggestive signals are worth more than one, but never conclusive.</summary>
    [Fact]
    public void TwoSuggestiveAgree_RaisesConfidence_ButNotToHigh()
    {
        var verdict = TopologyVerdict.From(
        [
            Says(TopologySignal.BridgeProtocolTraffic, TopologyFinding.Bridged, SignalStrength.Suggestive),
            Says(TopologySignal.LatencySlope, TopologyFinding.Bridged, SignalStrength.Suggestive),
        ]);

        Assert.Equal(TopologyConfidence.Moderate, verdict.Confidence);
        Assert.NotEqual(TopologyConfidence.High, verdict.Confidence);
    }

    /// <summary>Inconclusive signals are still reported, because a report has to say what ran.</summary>
    [Fact]
    public void InconclusiveSignalsSurvive_ButSortLast()
    {
        var verdict = TopologyVerdict.From(
        [
            TopologyObservation.Nothing(TopologySignal.LatencySlope, "slope within noise"),
            Says(TopologySignal.LinkSpeedMismatch, TopologyFinding.Bridged, SignalStrength.Conclusive),
        ]);

        Assert.Equal(2, verdict.Observations.Count);
        Assert.Equal(TopologySignal.LinkSpeedMismatch, verdict.Observations[0].Signal);
        Assert.Equal(TopologyFinding.Inconclusive, verdict.Observations[^1].Finding);
    }

    /// <summary>An unestablished topology says so, rather than describing a link it did not find.</summary>
    [Fact]
    public void UnknownSummary_SaysResultsCannotBeAttributed()
    {
        var verdict = TopologyVerdict.From([]);

        Assert.Contains("not established", verdict.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be attributed", verdict.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
