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
    /// Everything crossing argues for a direct link but never conclusively: a media converter, a
    /// PHY repeater and a passive tap are not 802.1 relay components and pass everything.
    /// </summary>
    [Fact]
    public void EverythingCrosses_ArguesDirect_ButNotConclusively()
    {
        var observation = ReservedMulticastProbe.Observe(
        [
            Sent(ProbeAddress.Control, crossed: true),
            Sent(ProbeAddress.SlowProtocols, crossed: true),
            Sent(ProbeAddress.MacControlProtocols, crossed: true),
            Sent(ProbeAddress.NearestBridge, crossed: true),
        ]);

        Assert.Equal(TopologyFinding.Direct, observation.Finding);
        Assert.Equal(SignalStrength.Strong, observation.Strength);
        Assert.NotEqual(SignalStrength.Conclusive, observation.Strength);
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
    /// A sweep that says "direct" cannot on its own license grading, because Strong on one signal
    /// is Moderate confidence and the model requires that plus no contradicting bridge evidence.
    /// </summary>
    [Fact]
    public void ADirectSweepAlone_IsModerateConfidence()
    {
        var verdict = TopologyVerdict.From(
        [
            ReservedMulticastProbe.Observe(
            [
                Sent(ProbeAddress.Control, crossed: true),
                Sent(ProbeAddress.SlowProtocols, crossed: true),
            ]),
        ]);

        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.Moderate, verdict.Confidence);
    }
}
