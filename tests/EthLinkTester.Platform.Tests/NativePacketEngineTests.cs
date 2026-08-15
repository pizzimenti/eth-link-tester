using EthLinkTester.Core;
using EthLinkTester.Core.Engine;
using EthLinkTester.Platform;

namespace EthLinkTester.Platform.Tests;

/// <summary>
/// Covers the part of the native engine that runs before any native call.
/// </summary>
/// <remarks>
/// Everything here executes on a machine with no Npcap, no adapters, and no engine DLL, which is
/// the point: these are the checks that must reject a bad configuration <em>instead of</em>
/// reaching hardware. A test that needed the rig would not run in CI, and this class exists
/// because the whole Platform assembly previously had no tests at all.
/// </remarks>
public sealed class NativePacketEngineTests
{
    private static readonly byte[] TxMac = [0x00, 0x1B, 0x21, 0x11, 0x22, 0x33];
    private static readonly byte[] RxMac = [0x00, 0x1B, 0x21, 0x44, 0x55, 0x66];

    private static NativePacketEngine Create() =>
        new(@"\Device\NPF_{A}", @"\Device\NPF_{B}", TxMac, RxMac);

    [Theory]
    [InlineData("", @"\Device\NPF_{B}")]
    [InlineData("   ", @"\Device\NPF_{B}")]
    [InlineData(@"\Device\NPF_{A}", "")]
    public void A_missing_device_name_is_rejected(string tx, string rx) =>
        Assert.Throws<ArgumentException>(() => new NativePacketEngine(tx, rx, TxMac, RxMac));

    [Fact]
    public void A_mac_that_is_not_six_bytes_is_rejected() =>
        Assert.Throws<ArgumentException>(
            () => new NativePacketEngine(@"\Device\NPF_{A}", @"\Device\NPF_{B}", [1, 2, 3], RxMac));

    /// <summary>
    /// One adapter cannot test a cable, and the failure without this check is the worst kind: the
    /// run starts, receives its own frames through the loopback path the whole design exists to
    /// avoid, and reports a perfect link.
    /// </summary>
    [Theory]
    [InlineData(@"\Device\NPF_{A}")]
    [InlineData(@"\device\npf_{a}")]
    public void The_same_adapter_for_both_directions_is_rejected(string rx) =>
        Assert.Throws<ArgumentException>(
            () => new NativePacketEngine(@"\Device\NPF_{A}", rx, TxMac, RxMac));

    [Theory]
    [InlineData("{1EA0DE30-5EA1-4CBC-A3C3-DEAD0F5B9F13}")]
    [InlineData("1EA0DE30-5EA1-4CBC-A3C3-DEAD0F5B9F13")]
    public void A_device_name_is_built_from_the_adapter_guid(string id) =>
        Assert.Equal(
            @"\Device\NPF_{1EA0DE30-5EA1-4CBC-A3C3-DEAD0F5B9F13}",
            NativePacketEngine.DeviceName(id));

    [Fact]
    public async Task Starting_after_disposal_is_refused()
    {
        var engine = Create();
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await engine.StartAsync(Settings));
    }

    /// <summary>
    /// Disposal has to be safe to repeat: the view model disposes on teardown, and the page it
    /// belongs to may already have done so when a run ended.
    /// </summary>
    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var engine = Create();

        await engine.DisposeAsync();
        await engine.DisposeAsync();

        Assert.Equal(EngineState.Idle, engine.State);
    }

    [Fact]
    public async Task Stopping_an_engine_that_never_started_leaves_it_idle()
    {
        var engine = Create();

        await engine.StopAsync();

        Assert.Equal(EngineState.Idle, engine.State);
        Assert.Null(engine.FaultDescription);
    }

    /// <summary>
    /// Draining an engine that holds no handle must be a no-op rather than a null dereference: the
    /// telemetry pump polls on a timer and does not stop the instant a run does.
    /// </summary>
    [Fact]
    public void Draining_an_idle_engine_yields_nothing()
    {
        var engine = Create();
        var buffer = new TelemetrySample[8];

        Assert.Equal(0, engine.Drain(buffer));
        Assert.Equal(0, engine.DroppedSamples);
    }

    [Fact]
    public async Task A_cancelled_start_throws_before_touching_the_engine()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Create().StartAsync(Settings, cancelled.Token));
    }

    private static EngineRunSettings Settings => new() { LinkSpeed = LinkSpeed.Mbps1000 };
}
