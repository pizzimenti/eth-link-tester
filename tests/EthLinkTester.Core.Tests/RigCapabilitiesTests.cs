using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class RigCapabilitiesTests
{
    private static NetworkAdapterInfo Adapter(
        string name,
        string description = "test adapter",
        AdapterStatus status = AdapterStatus.Up) => new()
    {
        Id = name,
        Name = name,
        Description = description,
        MacAddress = "00-00-00-00-00-00",
        Status = status,
    };

    private static AdapterCapabilities Caps(
        string id,
        LinkSpeed? max,
        IEnumerable<SpeedDuplex> forceable,
        LinkSpeed? negotiated = null,
        bool mdi = false) => new()
        {
            AdapterId = id,
            MaximumSpeed = max,
            NegotiatedSpeed = negotiated,
            ForceableSettings = [.. forceable],
            SupportsMdiControl = mdi,
        };

    /// <summary>The four combinations a typical driver exposes below gigabit.</summary>
    private static SpeedDuplex[] TenAndHundred() =>
    [
        new(LinkSpeed.Mbps10, DuplexMode.Half),
        new(LinkSpeed.Mbps10, DuplexMode.Full),
        new(LinkSpeed.Mbps100, DuplexMode.Half),
        new(LinkSpeed.Mbps100, DuplexMode.Full),
    ];

    /// <summary>
    /// The actual reference rig: an onboard Killer E2400 offering only 10 and 100, paired with a
    /// Realtek USB adapter that additionally offers a fixed 1 Gbps setting. Both linked at
    /// 1000BASE-T.
    /// </summary>
    private static RigCapabilities ReferenceRig() => RigCapabilities.Derive(
        Adapter("Ethernet", "Killer E2400 Gigabit Ethernet Controller"),
        Caps("Ethernet", LinkSpeed.Mbps1000, TenAndHundred(), negotiated: LinkSpeed.Mbps1000),
        Adapter("Ethernet 2", "Realtek USB GbE Family Controller"),
        Caps("Ethernet 2", LinkSpeed.Mbps1000,
            [.. TenAndHundred(), SpeedDuplex.Full(LinkSpeed.Mbps1000)],
            negotiated: LinkSpeed.Mbps1000));

    [Fact]
    public void CeilingIsTheSlowerAdaptersMaximum()
    {
        var rig = RigCapabilities.Derive(
            Adapter("fast"), Caps("fast", LinkSpeed.Mbps10000, []),
            Adapter("slow"), Caps("slow", LinkSpeed.Mbps1000, []));

        Assert.Equal(LinkSpeed.Mbps1000, rig.MaximumMutualSpeed);
    }

    [Fact]
    public void TestableSpeedsComeFromEvidenceOnBothEnds()
    {
        Assert.Equal(
            [LinkSpeed.Mbps10, LinkSpeed.Mbps100, LinkSpeed.Mbps1000],
            ReferenceRig().TestableSpeeds);
    }

    /// <summary>
    /// Regression: a ceiling does not imply every tier beneath it. X550-class adapters reach
    /// 10 Gbps and have no 10BASE-T at all, so inferring downwards would schedule a tier the
    /// hardware cannot run.
    /// </summary>
    [Fact]
    public void DoesNotInferTiersBelowTheCeiling()
    {
        SpeedDuplex[] tenGigNoTenMegabit =
        [
            new(LinkSpeed.Mbps100, DuplexMode.Full),
            SpeedDuplex.Full(LinkSpeed.Mbps1000),
            SpeedDuplex.Full(LinkSpeed.Mbps10000),
        ];

        var rig = RigCapabilities.Derive(
            Adapter("a"), Caps("a", LinkSpeed.Mbps10000, tenGigNoTenMegabit, negotiated: LinkSpeed.Mbps10000),
            Adapter("b"), Caps("b", LinkSpeed.Mbps10000, tenGigNoTenMegabit, negotiated: LinkSpeed.Mbps10000));

        Assert.DoesNotContain(LinkSpeed.Mbps10, rig.TestableSpeeds);
        Assert.False(rig.SupportsLongRunProbe);
    }

    /// <summary>
    /// Regression: with one adapter's maximum unknown - typically because its link is down -
    /// claiming the pair reaches the other's maximum asserts something nobody has observed.
    /// </summary>
    [Fact]
    public void CeilingIsUnknownWhenEitherEndIsUnknown()
    {
        var rig = RigCapabilities.Derive(
            Adapter("linked"), Caps("linked", LinkSpeed.Mbps10000, [], negotiated: LinkSpeed.Mbps10000),
            Adapter("down"), Caps("down", max: null, TenAndHundred()));

        Assert.Null(rig.MaximumMutualSpeed);
        Assert.Contains(rig.Limitations, l => l.Contains("maximum speed is unknown"));
    }

    /// <summary>
    /// Regression: a setting 802.3 forbids forcing must never appear as forceable, even when
    /// both drivers offer it. Two Realtek-style adapters would otherwise present an
    /// advertisement restriction as genuine pinning.
    /// </summary>
    [Fact]
    public void AdvertisementRestrictionsAreNotForceable()
    {
        var bothOfferGigabit = new[] { SpeedDuplex.Full(LinkSpeed.Mbps1000) }.Concat(TenAndHundred());

        var rig = RigCapabilities.Derive(
            Adapter("a"), Caps("a", LinkSpeed.Mbps1000, bothOfferGigabit, negotiated: LinkSpeed.Mbps1000),
            Adapter("b"), Caps("b", LinkSpeed.Mbps1000, bothOfferGigabit, negotiated: LinkSpeed.Mbps1000));

        Assert.DoesNotContain(SpeedDuplex.Full(LinkSpeed.Mbps1000), rig.ForceableSettings);
        Assert.Contains(SpeedDuplex.Full(LinkSpeed.Mbps1000), rig.AdvertisementRestrictedSettings);
        Assert.Contains(rig.Limitations, l => l.Contains("restrict advertised capability"));
    }

    /// <summary>
    /// Forcing one end alone does not pin the link, so a setting only counts when both offer it.
    /// On the reference rig that excludes the Realtek's gigabit entry twice over - once for not
    /// being mutual, once for not being truly forceable.
    /// </summary>
    [Fact]
    public void ForceableRequiresBothEndsToOfferTheSetting()
    {
        var rig = ReferenceRig();

        Assert.Equal(4, rig.ForceableSettings.Count);
        Assert.DoesNotContain(SpeedDuplex.Full(LinkSpeed.Mbps1000), rig.ForceableSettings);
        Assert.Empty(rig.AdvertisementRestrictedSettings);
        Assert.All(rig.ForceableSettings, s => Assert.True(s.IsTrulyForceable));
    }

    [Fact]
    public void LongRunProbeNeedsTenBaseTOnBothEnds()
    {
        Assert.True(ReferenceRig().SupportsLongRunProbe);

        var noTenMegabit = RigCapabilities.Derive(
            Adapter("a"), Caps("a", LinkSpeed.Mbps100, [SpeedDuplex.Full(LinkSpeed.Mbps100)]),
            Adapter("b"), Caps("b", LinkSpeed.Mbps100, [SpeedDuplex.Full(LinkSpeed.Mbps100)]));

        Assert.False(noTenMegabit.SupportsLongRunProbe);
    }

    [Fact]
    public void NamesTheAdapterThatSetsTheCeiling()
    {
        var rig = RigCapabilities.Derive(
            Adapter("fast", "Intel X550-T2"), Caps("fast", LinkSpeed.Mbps10000, []),
            Adapter("slow", "Killer E2400"), Caps("slow", LinkSpeed.Mbps1000, []));

        Assert.Contains(rig.Limitations, l => l.Contains("slow") && l.Contains("Killer E2400"));
    }

    [Fact]
    public void NoCeilingLimitationWhenTheRigReachesTenGigabit()
    {
        var rig = RigCapabilities.Derive(
            Adapter("a"), Caps("a", LinkSpeed.Mbps10000, [], mdi: true),
            Adapter("b"), Caps("b", LinkSpeed.Mbps10000, [], mdi: true));

        Assert.DoesNotContain(rig.Limitations, l => l.Contains("tops out at"));
    }

    [Fact]
    public void WarnsWhenNeitherAdapterHasMdiControl()
    {
        Assert.Contains(
            ReferenceRig().Limitations,
            l => l.StartsWith("Neither adapter exposes MDI", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: with one end able to set MDI/MDI-X the ambiguity is resolvable from that side,
    /// which is a materially different situation from neither end being able to. Saying "neither"
    /// when one adapter has the control is simply false, and it talks the user out of a
    /// diagnostic they could actually run.
    /// </summary>
    [Fact]
    public void NamesTheSingleAdapterLackingMdiControl()
    {
        var rig = RigCapabilities.Derive(
            Adapter("has-mdi"), Caps("has-mdi", LinkSpeed.Mbps1000, TenAndHundred(), mdi: true),
            Adapter("no-mdi"), Caps("no-mdi", LinkSpeed.Mbps1000, TenAndHundred(), mdi: false));

        Assert.Contains(rig.Limitations, l => l.StartsWith("no-mdi does not expose MDI", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Limitations, l => l.Contains("Neither adapter exposes MDI"));
    }

    [Fact]
    public void SupportedSpeedsCombineForceableAndNegotiatedEvidence()
    {
        // The Killer cannot force gigabit but is observably running it.
        var killer = Caps("k", LinkSpeed.Mbps1000, TenAndHundred(), negotiated: LinkSpeed.Mbps1000);

        Assert.Equal(
            [LinkSpeed.Mbps10, LinkSpeed.Mbps100, LinkSpeed.Mbps1000],
            killer.SupportedSpeeds);
    }

    /// <summary>
    /// Regression: a driver-reported maximum is evidence of support in its own right. Omitting
    /// it would leave a tier the hardware advertises out of the testable set.
    /// </summary>
    [Fact]
    public void SupportedSpeedsIncludeTheReportedMaximum()
    {
        var caps = Caps("a", LinkSpeed.Mbps2500, [SpeedDuplex.Full(LinkSpeed.Mbps100)]);

        Assert.Contains(LinkSpeed.Mbps2500, caps.SupportedSpeeds);
    }

    /// <summary>
    /// Regression: a downshifted link must not define the ceiling. Negotiation is the thing under
    /// test, so a degraded cable would otherwise make the NIC look like slower hardware and turn
    /// the exact fault this tool exists to find into a fixture limitation.
    /// </summary>
    /// <remarks>
    /// Both ends downshifted with no driver-reported maximum is the one case that stays genuinely
    /// unresolvable: nothing anywhere is evidence that either NIC is faster than the 100 Mbps it
    /// is currently running. The honest output is an unknown ceiling and an explicit caveat, not
    /// an invented gigabit tier - and it is what the PnPDeviceID lookup table in Phase 6 exists
    /// to resolve.
    /// </remarks>
    [Fact]
    public void DownshiftedLinkDoesNotBecomeTheCeiling()
    {
        var rig = RigCapabilities.Derive(
            Adapter("a"), Caps("a", max: null, TenAndHundred(), negotiated: LinkSpeed.Mbps100),
            Adapter("b"), Caps("b", max: null, TenAndHundred(), negotiated: LinkSpeed.Mbps100));

        // The ceiling is unknown rather than asserted as 100 Mbps...
        Assert.Null(rig.MaximumMutualSpeed);

        // ...and the user is told the evidence is incomplete rather than shown a hardware limit.
        Assert.Contains(rig.Limitations, l => l.Contains("maximum speed is unknown"));
        Assert.DoesNotContain(rig.Limitations, l => l.Contains("tops out at"));
    }

    /// <summary>
    /// Regression, and the one that matters most: an end with its link down has no evidence for
    /// gigabit - 802.3 forbids listing it as forceable and there is no negotiated speed to read -
    /// so intersecting the two ends' evidence let that silence veto the tier outright.
    /// </summary>
    /// <remarks>
    /// Reproduced on the reference rig by disabling one adapter: the Realtek still evidenced
    /// 1000BASE-T, the Killer went silent, and the rig came back with TestableSpeeds of exactly
    /// [10, 100]. The gigabit test was then never scheduled - which is to say the app stopped
    /// looking for the fault at precisely the moment a cable was bad enough to cause one.
    /// </remarks>
    [Fact]
    public void ASilentEndDoesNotVetoATierTheOtherEndEvidences()
    {
        var rig = RigCapabilities.Derive(
            // Link down: only the sub-gigabit forceable list, which proves nothing about gigabit.
            Adapter("Ethernet", status: AdapterStatus.Disconnected),
            Caps("Ethernet", max: null, TenAndHundred()),
            // Demonstrably gigabit-capable.
            Adapter("Ethernet 2", status: AdapterStatus.Disabled),
            Caps("Ethernet 2", max: null, [.. TenAndHundred(), SpeedDuplex.Full(LinkSpeed.Mbps1000)]));

        Assert.Contains(LinkSpeed.Mbps1000, rig.TestableSpeeds);
        Assert.Equal(
            [LinkSpeed.Mbps10, LinkSpeed.Mbps100, LinkSpeed.Mbps1000],
            rig.TestableSpeeds);
    }

    /// <summary>
    /// The counterpart to the rule above: below gigabit a driver's list is authoritative in both
    /// directions, so an end that genuinely lacks 10BASE-T still rules the tier out for the rig.
    /// </summary>
    [Fact]
    public void AnEndThatGenuinelyLacksATierStillRulesItOut()
    {
        var rig = RigCapabilities.Derive(
            Adapter("killer"), Caps("killer", LinkSpeed.Mbps1000, TenAndHundred(), negotiated: LinkSpeed.Mbps1000),
            Adapter("x550"), Caps("x550", LinkSpeed.Mbps1000,
                [new(LinkSpeed.Mbps100, DuplexMode.Full), SpeedDuplex.Full(LinkSpeed.Mbps1000)],
                negotiated: LinkSpeed.Mbps1000));

        Assert.DoesNotContain(LinkSpeed.Mbps10, rig.TestableSpeeds);
        Assert.False(rig.SupportsLongRunProbe);
    }

    /// <summary>
    /// An adapter that is not up cannot demonstrate what it supports, so the rig must say the
    /// probe was partial rather than present it as a complete result.
    /// </summary>
    [Fact]
    public void SaysSoWhenAnEndCouldNotBeFullyObserved()
    {
        var rig = RigCapabilities.Derive(
            Adapter("up"), Caps("up", LinkSpeed.Mbps1000, TenAndHundred(), negotiated: LinkSpeed.Mbps1000),
            Adapter("off", status: AdapterStatus.Disabled), Caps("off", max: null, TenAndHundred()));

        Assert.Contains(rig.Limitations, l => l.Contains("off is disabled"));
        Assert.DoesNotContain(rig.Limitations, l => l.Contains("up is"));
    }

    /// <summary>
    /// A loop needs two ports. The same adapter given as both ends derives a perfectly confident
    /// rig that cannot carry a single frame across a cable.
    /// </summary>
    [Fact]
    public void RejectsTheSameAdapterAsBothEnds()
    {
        var adapter = Adapter("Ethernet");
        var caps = Caps("Ethernet", LinkSpeed.Mbps1000, TenAndHundred());

        Assert.Throws<ArgumentException>(() => RigCapabilities.Derive(adapter, caps, adapter, caps));
    }
}
