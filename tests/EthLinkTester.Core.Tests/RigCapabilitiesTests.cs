using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class RigCapabilitiesTests
{
    private static NetworkAdapterInfo Adapter(string name, string description = "test adapter") => new()
    {
        Id = name,
        Name = name,
        Description = description,
        MacAddress = "00-00-00-00-00-00",
        Status = AdapterStatus.Up,
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
    public void WarnsWhenMdiControlIsUnavailable()
    {
        Assert.Contains(ReferenceRig().Limitations, l => l.Contains("MDI"));
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
}
