using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class ReservedMulticastProbeTests
{
    private static ProbeResult Sent(ProbeAddress address, bool crossed) => new(address, crossed);

    /// <summary>
    /// The control frame is what separates a measurement from a guess. Without it arriving, silence
    /// from a bridge and silence from a capture that never delivered anything are the same silence.
    /// </summary>
    [Fact]
    public void ControlDidNotArrive_IsInconclusive_NotBridged()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: false),
            Sent(ProbeAddress.SlowProtocols, crossed: false),
        ]);

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.NotEqual(TopologyFinding.Bridged, observation.Finding);
    }

    [Fact]
    public void ControlMissingEntirely_IsInconclusive()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.SlowProtocols, crossed: false),
        ]);

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
    }

    /// <summary>
    /// Control through, discriminator filtered: something is applying 802.1 filtering rules, which
    /// a cable cannot do.
    /// </summary>
    [Fact]
    public void ControlCrossesButSlowProtocolsIsFiltered_MeansBridged()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: false),
            Sent(ProbeAddress.MacControlProtocols, crossed: false),
            Sent(ProbeAddress.NearestBridge, crossed: false),
        ]);

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
    }

    /// <summary>
    /// Everything crossing argues for a direct link and only corroborates it: a media converter, a
    /// PHY repeater and a passive tap are not 802.1 relay components and pass everything.
    /// </summary>
    /// <remarks>
    /// The strength is the assertion that matters. Suggestive is below the bar
    /// <see cref="TopologyVerdict.From"/> requires to conclude Direct, so a sweep cannot reach a
    /// verdict on its own - which is what every prose statement about this signal has always said
    /// and what the code did not do while this branch returned Strong.
    /// </remarks>
    [Fact]
    public void EverythingCrosses_ArguesDirect_ButOnlyAsACorroborator()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: true),
            Sent(ProbeAddress.MacControlProtocols, crossed: true),
            Sent(ProbeAddress.NearestBridge, crossed: true),
        ]);

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
        Assert.Equal(SignalStrength.Suggestive, observation.Strength);
        Assert.True(observation.Strength < SignalStrength.Strong);
    }

    /// <summary>
    /// Filtered stays Strong while crossed does not, in one assertion, because the whole design of
    /// this signal is that its two branches are not worth the same.
    /// </summary>
    [Fact]
    public void FilteredOutweighsCrossed()
    {
        var filtered = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: false),
        ]);

        var crossed = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: true),
        ]);

        Assert.True(filtered.Strength > crossed.Strength);
    }

    /// <summary>
    /// The Realtek default strap: -00 and -03 onwards forwarded, -01 and -02 always filtered. The
    /// sweep must call this a bridge, and this is exactly the case the plan's original choice of
    /// 01:80:C2:00:00:00 would have reported as a direct cable.
    /// </summary>
    [Fact]
    public void RealtekDefaultStrap_IsCaughtByTheSlowProtocolsProbe()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            // Filtered even on default-forward silicon - non-overridable in both datasheets.
            Sent(ProbeAddress.SlowProtocols, crossed: false),
            // Forwarded by default on this family, which is why it cannot carry the verdict.
            Sent(ProbeAddress.MacControlProtocols, crossed: true),
            Sent(ProbeAddress.NearestBridge, crossed: true),
        ]);

        Assert.Equal(TopologyFinding.Bridged, observation.Finding);
        Assert.Contains("MacControlProtocols", observation.Detail);
        Assert.Contains("NearestBridge", observation.Detail);
    }

    /// <summary>The fingerprint is reported even when nothing reserved crossed.</summary>
    [Fact]
    public void FingerprintIsAlwaysStated()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: false),
        ]);

        Assert.Contains("No reserved address crossed", observation.Detail);
    }

    /// <summary>
    /// A sweep that says "direct" cannot on its own license grading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test used to state that claim in its docstring and then assert only the confidence
    /// value, which was Moderate - and Moderate is exactly where grading unlocks. Asserting what
    /// the docstring said would have failed. A test that documents a requirement and then asserts
    /// something weaker is worse than no test, because it certifies the gap.
    /// </para>
    /// <para>
    /// The verdict is now Unknown rather than a weaker Direct, and that is the right shape: the
    /// sweep cannot see a cable at all, only fail to see a bridge, and this model never converts
    /// absence of evidence into evidence of absence.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADirectSweepAlone_CannotLicenseGrading()
    {
        var verdict = TopologyVerdict.From(
        [
            ReservedMulticastProbe.Observe(
            [
                Sent(ProbeAddress.Control, crossed: true),
                Sent(ProbeAddress.SlowProtocols, crossed: true),
            ]),
        ]);

        Assert.False(verdict.GradingIsAttributable);
        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
    }

    /// <summary>
    /// What the sweep is for: lifting a signal that can see a cable from Moderate to no higher than
    /// Moderate, and standing behind it in the report.
    /// </summary>
    [Fact]
    public void ADirectSweep_CorroboratesASignalThatCanConclude()
    {
        var verdict = TopologyVerdict.From(
        [
            ReservedMulticastProbe.Observe(
            [
                Sent(ProbeAddress.Control, crossed: true),
                Sent(ProbeAddress.SlowProtocols, crossed: true),
            ]),
            new TopologyObservation(
                TopologySignal.ForcedSpeedAsymmetry,
                TopologyFinding.Direct,
                SignalStrength.Strong,
                "the free end followed the forced end down"),
        ]);

        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.Moderate, verdict.Confidence);
        Assert.True(verdict.GradingIsAttributable);
    }
}
