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

    private static AdapterBinding Bound(string id, string @class = "Transport", bool enabled = true) =>
        new(id, @class, enabled);

    /// <summary>
    /// The reference machine's actual bindings, which must come back clear.
    /// </summary>
    /// <remarks>
    /// Taken from the live provider on the reference rig, Npcap's own filter included. This is the
    /// regression that matters most: a rule that caveats a healthy machine puts a caveat on every
    /// report the tool ever produces, and a caveat everyone learns to ignore is worse than none.
    /// </remarks>
    [Fact]
    public void TheReferenceMachinesBindings_AreClear()
    {
        var status = SoftwareBridgeStatus.From(
            Adapter,
            [
                Bound("ms_msclient", "Client"),
                Bound("INSECURE_NPCAP", "Filter"),
                Bound("ms_pacer", "Filter"),
                Bound("ms_server", "Service"),
                Bound("ms_implat", enabled: false),
                Bound("ms_lldp"),
                Bound("ms_lltdio"),
                Bound("ms_rspndr"),
                Bound("ms_tcpip"),
                Bound("ms_tcpip6", enabled: false),
            ],
            anyTeamExists: false);

        Assert.Equal(SoftwareBridge.None, status.Bridge);
    }

    [Theory]
    [InlineData("vms_pp", SoftwareBridge.HyperVSwitch)]
    [InlineData("ms_bridge", SoftwareBridge.WindowsNetworkBridge)]
    public void ABlockingComponent_IsRecognisedByItsId(string componentId, SoftwareBridge expected)
    {
        var status = SoftwareBridgeStatus.From(
            Adapter, [Bound("ms_tcpip"), Bound(componentId)], anyTeamExists: false);

        Assert.Equal(expected, status.Bridge);
        Assert.True(status.BlocksRun);
    }

    /// <summary>
    /// The teaming protocol needs both halves: enabled here, and a team that exists.
    /// </summary>
    /// <remarks>
    /// <c>ms_implat</c> is bound on every adapter of the reference machine with no team configured
    /// anywhere, so the binding alone would refuse to test a perfectly ordinary NIC. Disabled is the
    /// state it is normally in, and an enabled one with no team is the state a broken team leaves
    /// behind - neither is a team.
    /// </remarks>
    [Theory]
    [InlineData(true, true, SoftwareBridge.NetworkTeam)]
    [InlineData(true, false, SoftwareBridge.None)]
    [InlineData(false, true, SoftwareBridge.None)]
    public void TeamingNeedsTheBindingEnabledAndATeamToExist(
        bool enabled, bool anyTeam, SoftwareBridge expected)
    {
        var status = SoftwareBridgeStatus.From(
            Adapter,
            [Bound("ms_tcpip"), Bound("ms_implat", enabled: enabled)],
            anyTeamExists: anyTeam);

        Assert.Equal(expected, status.Bridge);
    }

    /// <summary>
    /// An unknown protocol is a maybe; an unknown filter is not even that.
    /// </summary>
    /// <remarks>
    /// The class distinction is what keeps Npcap's own binding - enabled, on both reference
    /// adapters, and unknown to any allow-list of transports - from caveating every run.
    /// </remarks>
    [Theory]
    [InlineData("Transport", SoftwareBridge.UnrecognisedProtocolBinding)]
    [InlineData("Filter", SoftwareBridge.None)]
    [InlineData("Service", SoftwareBridge.None)]
    public void OnlyAProtocolClassBindingIsWorthMentioning(
        string componentClass, SoftwareBridge expected)
    {
        var status = SoftwareBridgeStatus.From(
            Adapter,
            [Bound("ms_tcpip"), Bound("iansprotocol", componentClass)],
            anyTeamExists: false);

        Assert.Equal(expected, status.Bridge);
    }

    /// <summary>A disabled binding is not in the stack, so it cannot be bridging anything.</summary>
    [Fact]
    public void ADisabledBridgeBindingIsNotABridge()
    {
        var status = SoftwareBridgeStatus.From(
            Adapter,
            [Bound("ms_tcpip"), Bound("ms_bridge", "Service", enabled: false)],
            anyTeamExists: false);

        Assert.Equal(SoftwareBridge.None, status.Bridge);
    }

    /// <summary>
    /// A blocking component wins over an unrecognised one, so the report names the thing that
    /// actually stops the run.
    /// </summary>
    [Fact]
    public void ABlockingComponentOutranksAnUnrecognisedOne()
    {
        var status = SoftwareBridgeStatus.From(
            Adapter,
            [Bound("iansprotocol"), Bound("vms_pp")],
            anyTeamExists: false);

        Assert.Equal(SoftwareBridge.HyperVSwitch, status.Bridge);
    }
}
