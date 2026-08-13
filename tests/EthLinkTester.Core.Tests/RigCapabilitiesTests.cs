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
        LinkSpeed max,
        IEnumerable<SpeedDuplex> forceable,
        bool mdi = false) => new()
        {
            AdapterId = id,
            MaximumSpeed = max,
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
    /// The actual reference rig: an onboard Killer E2400 that offers only 10 and 100, paired
    /// with a Realtek USB adapter that additionally offers a fixed 1 Gbps setting.
    /// </summary>
    private static RigCapabilities ReferenceRig() => RigCapabilities.Derive(
        Adapter("Ethernet", "Killer E2400 Gigabit Ethernet Controller"),
        Caps("Ethernet", LinkSpeed.Mbps1000, TenAndHundred()),
        Adapter("Ethernet 2", "Realtek USB GbE Family Controller"),
        Caps("Ethernet 2", LinkSpeed.Mbps1000,
            [.. TenAndHundred(), SpeedDuplex.Full(LinkSpeed.Mbps1000)]));

    [Fact]
    public void CeilingIsTheSlowerAdaptersMaximum()
    {
        var rig = RigCapabilities.Derive(
            Adapter("fast"), Caps("fast", LinkSpeed.Mbps10000, []),
            Adapter("slow"), Caps("slow", LinkSpeed.Mbps1000, []));

        Assert.Equal(LinkSpeed.Mbps1000, rig.MaximumMutualSpeed);
    }

    [Fact]
    public void TestableSpeedsStopAtTheCeiling()
    {
        var rig = ReferenceRig();

        Assert.Equal(
            [LinkSpeed.Mbps10, LinkSpeed.Mbps100, LinkSpeed.Mbps1000],
            rig.TestableSpeeds);
    }

    /// <summary>
    /// Forcing one end alone does not pin the link - the far end keeps negotiating. So a setting
    /// only counts as forceable for the rig when both adapters offer it, which on the reference
    /// rig excludes the Realtek's 1 Gbps entry.
    /// </summary>
    [Fact]
    public void ForceableRequiresBothEndsToOfferTheSetting()
    {
        var rig = ReferenceRig();

        Assert.Equal(4, rig.ForceableSettings.Count);
        Assert.DoesNotContain(SpeedDuplex.Full(LinkSpeed.Mbps1000), rig.ForceableSettings);
        Assert.Contains(new SpeedDuplex(LinkSpeed.Mbps100, DuplexMode.Full), rig.ForceableSettings);
    }

    [Fact]
    public void LongRunProbeNeedsTenBaseTOnBothEnds()
    {
        Assert.True(ReferenceRig().SupportsLongRunProbe);

        var noTenMegabit = RigCapabilities.Derive(
            Adapter("a"), Caps("a", LinkSpeed.Mbps10000, [SpeedDuplex.Full(LinkSpeed.Mbps100)]),
            Adapter("b"), Caps("b", LinkSpeed.Mbps10000, [SpeedDuplex.Full(LinkSpeed.Mbps100)]));

        Assert.False(noTenMegabit.SupportsLongRunProbe);
        Assert.Contains(noTenMegabit.Limitations, l => l.Contains("10BASE-T is unavailable"));
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

    /// <summary>
    /// Without MDI control a forced-speed link failure cannot be blamed on the cable, so the rig
    /// must say so rather than let the report imply a fault.
    /// </summary>
    [Fact]
    public void WarnsWhenMdiControlIsUnavailable()
    {
        Assert.Contains(ReferenceRig().Limitations, l => l.Contains("MDI"));
    }

    [Fact]
    public void ForcedGigabitIsReportedAsAdvertisementRestrictionNotForcing()
    {
        var realtek = Caps("Ethernet 2", LinkSpeed.Mbps1000,
            [.. TenAndHundred(), SpeedDuplex.Full(LinkSpeed.Mbps1000)]);

        // The driver offers it, but 802.3 Clause 40 means it cannot literally be forced.
        Assert.True(realtek.OffersAdvertisementRestriction);
        Assert.False(SpeedDuplex.Full(LinkSpeed.Mbps1000).IsTrulyForceable);
        Assert.True(new SpeedDuplex(LinkSpeed.Mbps100, DuplexMode.Full).IsTrulyForceable);
    }
}
