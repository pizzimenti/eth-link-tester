using EthLinkTester.Core;

namespace EthLinkTester.Core.Tests;

public class LinkSpeedTests
{
    public static TheoryData<LinkSpeed> AllSpeeds()
    {
        var data = new TheoryData<LinkSpeed>();
        foreach (var speed in Enum.GetValues<LinkSpeed>())
        {
            data.Add(speed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSpeeds))]
    public void EnumValueIsTheRateInMegabits(LinkSpeed speed)
    {
        Assert.Equal((long)speed, speed.MegabitsPerSecond());
        Assert.Equal((long)speed * 1_000_000L, speed.BitsPerSecond());
    }

    /// <summary>
    /// Driven over every enum member so adding a tier without naming it fails here rather than
    /// throwing at runtime inside a report.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSpeeds))]
    public void EverySpeedHasNames(LinkSpeed speed)
    {
        Assert.False(string.IsNullOrWhiteSpace(speed.StandardName()));
        Assert.False(string.IsNullOrWhiteSpace(speed.ShortName()));
    }

    [Theory]
    [InlineData(LinkSpeed.Mbps10, false)]
    [InlineData(LinkSpeed.Mbps100, false)]
    [InlineData(LinkSpeed.Mbps1000, true)]
    [InlineData(LinkSpeed.Mbps2500, true)]
    [InlineData(LinkSpeed.Mbps10000, true)]
    public void AutoNegotiationIsMandatoryAtGigabitAndAbove(LinkSpeed speed, bool expected)
    {
        // IEEE 802.3 Clause 40: 1000BASE-T needs negotiation to resolve master/slave clock
        // roles, so only 10 and 100 can genuinely be forced. Everything above is advertisement
        // restriction at best, and the test plan depends on that distinction.
        Assert.Equal(expected, speed.RequiresAutoNegotiation());
    }

    [Fact]
    public void StandardNamesMatchIeeeDesignations()
    {
        Assert.Equal("10BASE-T", LinkSpeed.Mbps10.StandardName());
        Assert.Equal("1000BASE-T", LinkSpeed.Mbps1000.StandardName());
        Assert.Equal("2.5GBASE-T", LinkSpeed.Mbps2500.StandardName());
        Assert.Equal("10GBASE-T", LinkSpeed.Mbps10000.StandardName());
    }
}
