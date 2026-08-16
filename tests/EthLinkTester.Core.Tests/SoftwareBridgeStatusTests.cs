using EthLinkTester.Core.Preflight;

namespace EthLinkTester.Core.Tests;

public class SoftwareBridgeStatusTests
{
    private const string Adapter = "{503593B4-FF35-43DB-BE33-B8D9AF979B28}";

    [Fact]
    public void Clear_BlocksNothingAndNeedsNoCaveat()
    {
        var status = SoftwareBridgeStatus.Clear(Adapter);

        Assert.Equal(SoftwareBridge.None, status.Bridge);
        Assert.False(status.BlocksRun);
        Assert.False(status.NeedsCaveat);
    }

    /// <summary>
    /// The three conditions where the frames may never reach a wire. A run on such an adapter is
    /// not a caveated measurement, it is a measurement of something else.
    /// </summary>
    [Theory]
    [InlineData(SoftwareBridge.HyperVSwitch, "vms_pp")]
    [InlineData(SoftwareBridge.WindowsNetworkBridge, "ms_bridge")]
    [InlineData(SoftwareBridge.NetworkTeam, "ms_implat")]
    public void AKnownSoftwareBridge_BlocksTheRun(SoftwareBridge bridge, string componentId)
    {
        var status = new SoftwareBridgeStatus(bridge, Adapter, componentId, "found");

        Assert.True(status.BlocksRun);
        Assert.False(status.NeedsCaveat);
    }

    /// <summary>
    /// A maybe belongs on the report, not in a refusal. Enumerating every vendor's component id is
    /// a losing game, and refusing on any unknown binding would break against the next NIC suite.
    /// </summary>
    [Fact]
    public void AnUnrecognisedBinding_IsCaveatedRatherThanRefused()
    {
        var status = new SoftwareBridgeStatus(
            SoftwareBridge.UnrecognisedProtocolBinding, Adapter, "iansprotocol", "found");

        Assert.False(status.BlocksRun);
        Assert.True(status.NeedsCaveat);
    }

    /// <summary>
    /// The component id is carried because that is what was matched. Display names are indirect
    /// MUI resources resolved at query time - one on the reference machine fails to resolve and
    /// comes back as the literal `@%windir%\System32\drivers\vwififlt.sys,-105` - so matching on
    /// them is locale- and build-fragile.
    /// </summary>
    [Fact]
    public void TheComponentIdIsCarried_BecauseThatIsWhatWasMatched()
    {
        var status = new SoftwareBridgeStatus(
            SoftwareBridge.WindowsNetworkBridge, Adapter, "ms_bridge", "found");

        Assert.Equal("ms_bridge", status.ComponentId);
    }
}
