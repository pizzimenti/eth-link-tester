using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class ReservedMulticastProbeTests
{
    /// <summary>What the engine sends per address.</summary>
    private const int Repeats = 20;

    private static ProbeResult Sent(ProbeAddress address, bool crossed) =>
        new(address, Repeats, crossed ? Repeats : 0);

    private static ProbeResult Sent(ProbeAddress address, int arrived) =>
        new(address, Repeats, arrived);

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
    /// The converse gate. A path losing most of its frames loses the whole discriminator probe by
    /// chance often enough to matter - at 5% delivery, 0.95²⁰ is about a third of the time - and
    /// the old code called that filtering, because one control frame in twenty was enough to
    /// declare the path healthy.
    /// </summary>
    /// <remarks>
    /// Wrong in the cheap direction, and stated at Strong with no hedge: a marginal cable on a
    /// direct rig produced intermittent verdicts implicating a switch nobody owned. This is exactly
    /// the cable this tool exists to find, so getting it wrong here is not an edge case.
    /// </remarks>
    [Fact]
    public void ALossyPathWithNothingOnTheDiscriminator_IsInconclusive_NotBridged()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, arrived: 1),
            Sent(ProbeAddress.SlowProtocols, arrived: 0),
        ]);

        Assert.Equal(TopologyFinding.Inconclusive, observation.Finding);
        Assert.NotEqual(TopologyFinding.Bridged, observation.Finding);
        Assert.Contains("1 of 20", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate is on delivery quality, not perfection: a few lost control frames still leave the
    /// discriminator's silence meaning something.
    /// </summary>
    [Theory]
    [InlineData(20, TopologyFinding.Bridged)]
    [InlineData(15, TopologyFinding.Bridged)]
    [InlineData(14, TopologyFinding.Inconclusive)]
    [InlineData(5, TopologyFinding.Inconclusive)]
    public void TheBridgedVerdictNeedsAHealthyControl(int controlArrived, TopologyFinding expected)
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, arrived: controlArrived),
            Sent(ProbeAddress.SlowProtocols, arrived: 0),
        ]);

        Assert.Equal(expected, observation.Finding);
    }

    /// <summary>
    /// One arrival is enough to prove forwarding even on a lossy path, and the asymmetry is the
    /// point: filtering is not a lossy process, so a frame that got through was not filtered.
    /// </summary>
    [Fact]
    public void OneArrivalProvesForwarding_EvenWhenTheControlIsLossy()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, arrived: 2),
            Sent(ProbeAddress.SlowProtocols, arrived: 1),
        ]);

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
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
        Assert.Contains("MacControlProtocols", observation.Detail, StringComparison.Ordinal);
        Assert.Contains("NearestBridge", observation.Detail, StringComparison.Ordinal);
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

        Assert.Contains("No reserved address crossed", observation.Detail, StringComparison.Ordinal);
    }

    /// <summary>Every verdict states the delivery it rests on, so a report can be argued with.</summary>
    [Fact]
    public void TheDetailStatesHowManyFramesArrived()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, arrived: 20),
            Sent(ProbeAddress.SlowProtocols, arrived: 0),
        ]);

        Assert.Contains("20 of 20", observation.Detail, StringComparison.Ordinal);
        Assert.Contains("0 of 20", observation.Detail, StringComparison.Ordinal);
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
    /// What the sweep is for: standing behind a signal that can see a cable, and being named in the
    /// report alongside it.
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
