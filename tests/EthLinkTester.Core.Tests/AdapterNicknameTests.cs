using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class AdapterNicknameTests
{
    /// <summary>
    /// Real driver descriptions. The two marked "reference rig" are the strings this machine
    /// actually reports and were read from the live adapters rather than written from memory.
    /// </summary>
    [Theory]
    // reference rig
    [InlineData("Killer E2400 Gigabit Ethernet Controller", "Killer E2400")]
    [InlineData("Realtek USB GbE Family Controller", "Realtek USB GbE")]
    // the 10G card the plan targets for a later rig
    [InlineData("Intel(R) Ethernet Controller X550-T2", "Intel X550-T2")]
    [InlineData("Intel(R) I211 Gigabit Network Connection", "Intel I211")]
    [InlineData("Intel(R) 82579LM Gigabit Network Connection", "Intel 82579LM")]
    // the USB adapters recommended for a portable rig
    [InlineData("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter", "ASIX AX88179 USB 3.0")]
    [InlineData("Realtek PCIe GbE Family Controller", "Realtek PCIe GbE")]
    [InlineData("Realtek USB 2.5GbE Family Controller", "Realtek USB 2.5GbE")]
    [InlineData("Aquantia AQtion 10Gbit Network Adapter", "Aquantia AQtion 10Gbit")]
    [InlineData("Killer E3100G 2.5 Gigabit Ethernet Controller", "Killer E3100G 2.5")]
    [InlineData("Intel(R) Ethernet Adapter Controller", "Intel")]
    public void ShortensRealDriverDescriptions(string description, string expected) =>
        Assert.Equal(expected, AdapterNickname.From(description));

    /// <summary>
    /// Regression: a speed is identity, not boilerplate. Folding "10Gbit" into a noise phrase
    /// collapsed a 10G card to the same nickname a 5G one keeps, which is the exact failure a
    /// nickname exists to prevent - two ports that cannot be told apart.
    /// </summary>
    [Fact]
    public void NeverStripsTheSpeedThatDistinguishesTwoCardsFromOneVendor()
    {
        var tenGig = AdapterNickname.From("Aquantia AQtion 10Gbit Network Adapter");
        var fiveGig = AdapterNickname.From("Aquantia AQtion 5Gbit Network Adapter");

        Assert.Contains("10Gbit", tenGig, StringComparison.Ordinal);
        Assert.NotEqual(tenGig, fiveGig);
    }

    /// <summary>
    /// Regression: a plain substring search cut "PCI Express" out of "PCI Expressway" and left
    /// "way". A mangled name that still looks like a name is worse than no shortening at all.
    /// </summary>
    [Theory]
    [InlineData("Vendor PCI Expressway Card", "Vendor PCI Expressway Card")]
    [InlineData("NetworkAdapter Pro", "NetworkAdapter Pro")]
    [InlineData("Contoso Adapterless Bridge", "Contoso Adapterless Bridge")]
    public void OnlyRemovesBoilerplateStandingAsWholeWords(string description, string expected) =>
        Assert.Equal(expected, AdapterNickname.From(description));

    /// <summary>
    /// Regression, and the reason the previous test of this was worthless: a phrase removed from
    /// the middle strands its connective there, not at the end. The real ASIX INF spells the bus
    /// "USB2.0" with no space, so it misses the bus phrase and hits "Fast Ethernet Adapter"
    /// instead - leaving "ASIX AX88772C USB2.0 to".
    /// </summary>
    [Fact]
    public void DropsAConnectiveStrandedInTheMiddle()
    {
        var nickname = AdapterNickname.From("ASIX AX88772C USB2.0 to Fast Ethernet Adapter");

        Assert.Equal("ASIX AX88772C USB2.0", nickname);
        Assert.DoesNotContain(" to", nickname, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "to" separates what a device is from what it converts to, so the bus in front of it is
    /// kept - USB 3.0 and USB 2.0 ASIX adapters are different parts - and the class description
    /// behind it, shared by every adapter of that kind, is dropped.
    /// </summary>
    [Fact]
    public void KeepsTheBusButNotTheConversionItPerforms()
    {
        Assert.Equal(
            "ASIX AX88179 USB 3.0",
            AdapterNickname.From("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter"));

        Assert.NotEqual(
            AdapterNickname.From("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter"),
            AdapterNickname.From("ASIX AX88772C USB2.0 to Fast Ethernet Adapter"));
    }

    /// <summary>
    /// Windows appends a suffix when two identical cards are present, and that suffix is the only
    /// thing distinguishing them - exactly the case a nickname exists to serve.
    /// </summary>
    [Fact]
    public void KeepsTheSuffixThatDistinguishesIdenticalCards()
    {
        Assert.Equal("Intel X550-T2 #2", AdapterNickname.From("Intel(R) Ethernet Controller X550-T2 #2"));
    }

    /// <summary>
    /// The failure that matters: a description this does not recognise must survive intact. A
    /// slightly long nickname is cosmetic; a wrong one sends someone to unplug the wrong cable.
    /// </summary>
    [Fact]
    public void KeepsAnUnrecognisedDescriptionUnchanged()
    {
        const string Odd = "SomeVendor 12345 Widget";

        Assert.Equal(Odd, AdapterNickname.From(Odd));
    }

    /// <summary>A description of nothing but boilerplate must not shorten to nothing.</summary>
    [Fact]
    public void KeepsADescriptionThatIsEntirelyBoilerplate()
    {
        Assert.Equal("Ethernet Controller", AdapterNickname.From("Ethernet Controller"));
    }


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackWhenThereIsNoDescription(string? description) =>
        Assert.Equal("Ethernet 2", AdapterNickname.From(description, "Ethernet 2"));

    [Fact]
    public void FallsBackToEmptyRatherThanNullWhenNothingIsAvailable() =>
        Assert.Equal(string.Empty, AdapterNickname.From(null));

}
