using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class SpeedDuplexParserTests
{
    [Theory]
    [InlineData("10 Mbps Half Duplex", LinkSpeed.Mbps10, DuplexMode.Half)]
    [InlineData("10 Mbps Full Duplex", LinkSpeed.Mbps10, DuplexMode.Full)]
    [InlineData("100 Mbps Half Duplex", LinkSpeed.Mbps100, DuplexMode.Half)]
    [InlineData("100 Mbps Full Duplex", LinkSpeed.Mbps100, DuplexMode.Full)]
    [InlineData("1.0 Gbps Full Duplex", LinkSpeed.Mbps1000, DuplexMode.Full)]
    [InlineData("2.5 Gbps Full Duplex", LinkSpeed.Mbps2500, DuplexMode.Full)]
    [InlineData("10 Gbps Full Duplex", LinkSpeed.Mbps10000, DuplexMode.Full)]
    public void ParsesRealDriverStrings(string input, LinkSpeed speed, DuplexMode duplex)
    {
        Assert.True(SpeedDuplexParser.TryParse(input, out var setting));
        Assert.Equal(new SpeedDuplex(speed, duplex), setting);
    }

    /// <summary>
    /// Auto-negotiation is the absence of a forced setting, not a setting, so rejecting it here
    /// keeps it out of the forceable list where it would misrepresent what the rig can pin.
    /// </summary>
    [Theory]
    [InlineData("Auto Negotiation")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Best Effort")]
    [InlineData("1000 Mbps")]
    [InlineData("100 Mbps Full")]
    public void RejectsNonSettings(string? input)
    {
        Assert.False(SpeedDuplexParser.TryParse(input, out _));
    }

    /// <summary>A tier the app has no LinkSpeed for is ignored rather than guessed at.</summary>
    [Fact]
    public void IgnoresUnknownTiers()
    {
        Assert.False(SpeedDuplexParser.TryParse("40 Gbps Full Duplex", out _));
        Assert.False(SpeedDuplexParser.TryParse("200 Mbps Full Duplex", out _));
    }

    [Fact]
    public void ParsesCaseInsensitivelyAndToleratesSpacing()
    {
        Assert.True(SpeedDuplexParser.TryParse("  100 mbps full duplex  ", out var setting));
        Assert.Equal(new SpeedDuplex(LinkSpeed.Mbps100, DuplexMode.Full), setting);
    }

    /// <summary>The exact list the onboard Killer E2400 reports on the reference machine.</summary>
    [Fact]
    public void ParsesTheKillerE2400ValueList()
    {
        string[] driverValues =
        [
            "Auto Negotiation",
            "10 Mbps Half Duplex",
            "10 Mbps Full Duplex",
            "100 Mbps Half Duplex",
            "100 Mbps Full Duplex",
        ];

        var parsed = SpeedDuplexParser.ParseAll(driverValues);

        Assert.Equal(4, parsed.Count);
        Assert.DoesNotContain(parsed, s => s.Speed == LinkSpeed.Mbps1000);
        Assert.All(parsed, s => Assert.True(s.IsTrulyForceable));
    }

    /// <summary>
    /// The Realtek USB adapter's list, which adds a fixed 1 Gbps entry. It parses, but 802.3
    /// forbids literally forcing it - the driver is restricting advertisement.
    /// </summary>
    [Fact]
    public void ParsesTheRealtekListAndFlagsGigabitAsNotTrulyForceable()
    {
        string[] driverValues =
        [
            "Auto Negotiation",
            "10 Mbps Half Duplex",
            "10 Mbps Full Duplex",
            "100 Mbps Half Duplex",
            "100 Mbps Full Duplex",
            "1.0 Gbps Full Duplex",
        ];

        var parsed = SpeedDuplexParser.ParseAll(driverValues);

        Assert.Equal(5, parsed.Count);
        var gigabit = Assert.Single(parsed, s => s.Speed == LinkSpeed.Mbps1000);
        Assert.False(gigabit.IsTrulyForceable);
    }
}
