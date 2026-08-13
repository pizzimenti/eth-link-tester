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
    [InlineData("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter", "ASIX AX88179")]
    [InlineData("Realtek PCIe GbE Family Controller", "Realtek PCIe GbE")]
    [InlineData("Realtek USB 2.5GbE Family Controller", "Realtek USB 2.5GbE")]
    [InlineData("Aquantia AQtion 10Gbit Network Adapter", "Aquantia AQtion")]
    public void ShortensRealDriverDescriptions(string description, string expected) =>
        Assert.Equal(expected, AdapterNickname.From(description));

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

    /// <summary>
    /// Removing a trailing phrase can leave the connective that introduced it, which reads as a
    /// truncation rather than a name.
    /// </summary>
    [Fact]
    public void DoesNotLeaveADanglingConnective()
    {
        var nickname = AdapterNickname.From("ASIX AX88179 USB 3.0 to Gigabit Ethernet Adapter");

        Assert.DoesNotContain(" to", nickname, StringComparison.OrdinalIgnoreCase);
        Assert.False(nickname.EndsWith('-'));
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

    /// <summary>
    /// A nickname must never be so aggressive that two different adapters collapse to the same
    /// text - the whole point is telling one port from the other.
    /// </summary>
    [Fact]
    public void KeepsTheReferenceRigsTwoAdaptersDistinguishable()
    {
        var killer = AdapterNickname.From("Killer E2400 Gigabit Ethernet Controller");
        var realtek = AdapterNickname.From("Realtek USB GbE Family Controller");

        Assert.NotEqual(killer, realtek);
    }
}
