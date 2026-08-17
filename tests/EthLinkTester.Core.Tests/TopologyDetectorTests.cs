using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Safety;
using EthLinkTester.Core.Topology;

namespace EthLinkTester.Core.Tests;

public class TopologyDetectorTests
{
    private const string TxId = "{tx}";
    private const string RxId = "{rx}";
    private const long Gigabit = 1_000_000_000;
    private const long Fast = 100_000_000;

    /// <summary>
    /// A rig whose adapters, configurator and wire probe are all under the test's control, and
    /// which records what was asked of it.
    /// </summary>
    /// <remarks>
    /// The forced-speed cycle is modelled rather than stubbed: forcing the speed actually changes
    /// what the provider reports, and restoring puts it back. Otherwise the tests would assert on
    /// call sequences instead of on the verdict, and the whole question here is whether the right
    /// verdict comes out of a plausible sequence of hardware states.
    /// </remarks>
    private sealed class Rig : IAdapterProvider, IAdapterConfigurator, ITopologyProbe
    {
        public Dictionary<string, long> Speeds { get; } = new()
        {
            [TxId] = Gigabit,
            [RxId] = Gigabit,
        };

        public Dictionary<string, DuplexMode> Duplex { get; } = new()
        {
            [TxId] = DuplexMode.Full,
            [RxId] = DuplexMode.Full,
        };

        public HashSet<string> DefaultRoute { get; } = [];

        /// <summary>What the far end does when the near end is forced. Set per test.</summary>
        public Action<Rig>? OnForce { get; set; }

        /// <summary>Set to make the driver refuse the forced setting.</summary>
        public string? ForceRefusal { get; set; }

        public List<ProbeResult> SweepResults { get; } = [];

        public PassiveListenResult Heard { get; set; }

        public Exception? SweepFailure { get; set; }

        public List<string> Forced { get; } = [];

        public List<RestoreScope> Restores { get; } = [];

        public long TimestampFrequency => TimeSpan.TicksPerSecond;

        public Task<IReadOnlyList<NetworkAdapterInfo>> GetPhysicalAdaptersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NetworkAdapterInfo>>(
            [
                .. Speeds.Select(pair => new NetworkAdapterInfo
                {
                    Id = pair.Key,
                    Name = pair.Key == TxId ? "Ethernet" : "Ethernet 2",
                    Description = "test adapter",
                    MacAddress = "00-00-00-00-00-00",
                    Status = pair.Value > 0 ? AdapterStatus.Up : AdapterStatus.Disconnected,
                    LinkSpeedBitsPerSecond = pair.Value,
                    Duplex = Duplex[pair.Key],
                    CarriesDefaultRoute = DefaultRoute.Contains(pair.Key),
                }),
            ]);

        public Task<AdapterCapabilities> ProbeCapabilitiesAsync(
            string adapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdapterCounters?> ReadCountersAsync(
            string adapterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AdapterCounters?>(null);

        public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
            string adapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConfigurationOutcome> ApplyAsync(
            NetworkAdapterInfo adapter,
            string keyword,
            string registryValue,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConfigurationOutcome> ForceSpeedAsync(
            NetworkAdapterInfo adapter,
            SpeedDuplex setting,
            CancellationToken cancellationToken = default)
        {
            if (ForceRefusal is not null)
            {
                throw new InvalidOperationException(ForceRefusal);
            }

            Forced.Add(adapter.Id);
            Speeds[adapter.Id] = setting.Speed.BitsPerSecond();
            Duplex[adapter.Id] = setting.Duplex;
            OnForce?.Invoke(this);

            return Task.FromResult(ConfigurationOutcome.Applied);
        }

        public Task<IReadOnlyList<ProbeResult>> SweepAsync(
            string transmitAdapterId,
            string receiveAdapterId,
            CancellationToken cancellationToken = default) =>
            SweepFailure is not null
                ? Task.FromException<IReadOnlyList<ProbeResult>>(SweepFailure)
                : Task.FromResult<IReadOnlyList<ProbeResult>>([.. SweepResults]);

        public Task<PassiveListenResult> ListenAsync(
            IReadOnlyList<string> adapterIds,
            TimeSpan window,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Heard);

        public Task<RestoreOutcome> RestoreAsync(
            RestoreScope scope, CancellationToken cancellationToken = default)
        {
            Restores.Add(scope);

            foreach (var id in Forced)
            {
                Speeds[id] = Gigabit;
                Duplex[id] = DuplexMode.Full;
            }

            return Task.FromResult(RestoreOutcome.Empty);
        }
    }

    private static (Rig Rig, TopologyDetector Detector) Build()
    {
        var rig = new Rig();
        return (rig, new TopologyDetector(rig, rig, rig, new TestClock()));
    }

    private static TopologyDetectionRequest Request(
        bool disruptive = false, TimeSpan? passive = null) => new()
    {
        TransmitAdapterId = TxId,
        ReceiveAdapterId = RxId,
        AllowDisruptive = disruptive,
        PassiveWindow = passive ?? TimeSpan.Zero,
        LinkSettleTimeout = TimeSpan.Zero,
    };

    private static void CrossingSweep(Rig rig) =>
        rig.SweepResults.AddRange(
        [
            new(ProbeAddress.Control, 20, 20),
            new(ProbeAddress.SlowProtocols, 20, 20),
            new(ProbeAddress.MacControlProtocols, 20, 20),
            new(ProbeAddress.NearestBridge, 20, 20),
        ]);

    private static void FilteredSweep(Rig rig) =>
        rig.SweepResults.AddRange(
        [
            new(ProbeAddress.Control, 20, 20),
            new(ProbeAddress.SlowProtocols, 20, 0),
            new(ProbeAddress.MacControlProtocols, 20, 0),
            new(ProbeAddress.NearestBridge, 20, 0),
        ]);

    /// <summary>
    /// Every signal is in the result, whether or not it ran.
    /// </summary>
    /// <remarks>
    /// A report that omits the tests it skipped is claiming coverage it does not have. RFC 2544
    /// section 7 asks for the configuration actually used, and what was disabled is part of it.
    /// </remarks>
    [Fact]
    public async Task EverySignalIsReported_IncludingTheOnesThatDidNotRun()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);

        var verdict = await detector.DetectAsync(Request());

        Assert.Equal(
            Enum.GetValues<TopologySignal>().Length,
            verdict.Observations.Select(o => o.Signal).Distinct().Count());
        Assert.Contains(verdict.Observations, o => !o.Ran);
        Assert.All(verdict.Observations, o => Assert.False(string.IsNullOrWhiteSpace(o.Detail)));
    }

    /// <summary>
    /// The default answer on a healthy direct rig with the disruptive tests declined: Unknown.
    /// </summary>
    /// <remarks>
    /// Both ports at a gigabit and every reserved address crossing is exactly what a direct cable
    /// looks like - and exactly what a media converter or a PHY repeater looks like. Nothing that
    /// ran can tell them apart, so the honest answer is that the topology is not established, and
    /// nothing may be graded against it. This is the case a scoring model would get wrong.
    /// </remarks>
    [Fact]
    public async Task AQuietDirectRig_WithoutDisruptiveTests_IsUnknown()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);

        var verdict = await detector.DetectAsync(Request());

        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
        Assert.False(verdict.GradingIsAttributable);
        Assert.Empty(rig.Forced);
    }

    /// <summary>
    /// And with them allowed, the same rig reaches a graded Direct - the whole reason the probe
    /// is worth its disruption.
    /// </summary>
    [Fact]
    public async Task AQuietDirectRig_WithDisruptiveTests_IsDirectAndGradable()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);

        // A cable: the far end follows the forced end down, and comes up half duplex because
        // parallel detection carries speed but not duplex.
        rig.OnForce = r =>
        {
            r.Speeds[RxId] = Fast;
            r.Duplex[RxId] = DuplexMode.Half;
        };

        var verdict = await detector.DetectAsync(Request(disruptive: true));

        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
        Assert.True(verdict.GradingIsAttributable);
        Assert.Equal([TxId], rig.Forced);
    }

    /// <summary>The far end staying put is one cable's worth of proof that it is not one cable.</summary>
    [Fact]
    public async Task AFarEndThatDoesNotFollow_IsBridgedAtHighConfidence()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.OnForce = _ => { };

        var verdict = await detector.DetectAsync(Request(disruptive: true));

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.High, verdict.Confidence);
    }

    /// <summary>
    /// The restore runs, is scoped to the one property, and happens even when the probe throws.
    /// </summary>
    /// <remarks>
    /// Scoped because a global restore would revert whatever else the surrounding run had
    /// configured. In a finally because an unrestored adapter is worse than a missing measurement -
    /// especially the one carrying the default route.
    /// </remarks>
    [Fact]
    public async Task TheForcedSettingIsRestored_ScopedToThatOneProperty()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.OnForce = r => r.Speeds[RxId] = Fast;

        await detector.DetectAsync(Request(disruptive: true));

        var restore = Assert.Single(rig.Restores);
        Assert.False(restore.IsEverything);
        Assert.Equal(Gigabit, rig.Speeds[TxId]);
    }

    /// <summary>
    /// The port carrying the default route is the one left alone.
    /// </summary>
    /// <remarks>
    /// Both choices are defensible for the measurement and only one of them is defensible for the
    /// person using the machine: forcing the interface carrying their internet connection drops it
    /// for the length of the probe, and for longer if the app dies in between.
    /// </remarks>
    [Fact]
    public async Task TheDefaultRouteAdapterIsNotTheOneForced()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.DefaultRoute.Add(TxId);
        rig.OnForce = r =>
        {
            r.Speeds[TxId] = Fast;
            r.Duplex[TxId] = DuplexMode.Half;
        };

        var verdict = await detector.DetectAsync(Request(disruptive: true));

        Assert.Equal([RxId], rig.Forced);
        Assert.Equal(TopologyConclusion.Direct, verdict.Conclusion);
    }

    /// <summary>
    /// A settled conclusive bridge is not worth bouncing a link to re-prove.
    /// </summary>
    [Fact]
    public async Task AConclusiveBridgeFromTheFreeSignal_SkipsTheDisruptiveProbe()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.Speeds[RxId] = Fast;

        var verdict = await detector.DetectAsync(Request(disruptive: true));

        Assert.Empty(rig.Forced);
        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);

        var skipped = verdict.Observations.Single(
            o => o.Signal == TopologySignal.ForcedSpeedAsymmetry);

        Assert.False(skipped.Ran);
        Assert.Contains("already negotiated different speeds", skipped.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver that will not take the setting produces a stated non-result, not an exception.
    /// </summary>
    /// <remarks>
    /// Common rather than exotic: plenty of drivers offer no fixed 100 Mbps at all, and one on the
    /// reference rig advertises a fixed gigabit it does not honour.
    /// </remarks>
    [Fact]
    public async Task ADriverThatRefusesTheForcedSetting_IsReportedNotThrown()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.ForceRefusal = "'Ethernet' does not offer 100 Mbps Full Duplex.";

        var verdict = await detector.DetectAsync(Request(disruptive: true));

        var observation = verdict.Observations.Single(
            o => o.Signal == TopologySignal.ForcedSpeedAsymmetry);

        Assert.False(observation.Ran);
        Assert.Contains("does not offer", observation.Detail, StringComparison.Ordinal);
        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
    }

    /// <summary>
    /// A sweep that could not run is not a sweep that found nothing.
    /// </summary>
    [Fact]
    public async Task AFailedSweep_IsNotRunRatherThanEvidence()
    {
        var (rig, detector) = Build();
        rig.SweepFailure = new InvalidOperationException("Npcap is not installed");

        var verdict = await detector.DetectAsync(Request());

        var observation = verdict.Observations.Single(
            o => o.Signal == TopologySignal.ReservedMulticastProbe);

        Assert.False(observation.Ran);
        Assert.Contains("Npcap is not installed", observation.Detail, StringComparison.Ordinal);
        Assert.Equal(TopologyConclusion.Unknown, verdict.Conclusion);
    }

    /// <summary>Hearing a switch announce itself beats everything the sweep can say.</summary>
    [Fact]
    public async Task HearingLldp_IsBridged_EvenWhenTheSweepCrossed()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);
        rig.Heard = new PassiveListenResult(TimeSpan.FromSeconds(200), Lldp: 4, Stp: 0, Cdp: 0);

        var verdict = await detector.DetectAsync(
            Request(passive: TimeSpan.FromSeconds(200)));

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Contains("LLDP", verdict.Summary, StringComparison.Ordinal);
    }

    /// <summary>A filtered sweep is a bridge on its own, at Strong.</summary>
    [Fact]
    public async Task AFilteredSweep_IsBridged()
    {
        var (rig, detector) = Build();
        FilteredSweep(rig);

        var verdict = await detector.DetectAsync(Request());

        Assert.Equal(TopologyConclusion.Bridged, verdict.Conclusion);
        Assert.Equal(TopologyConfidence.Moderate, verdict.Confidence);
    }

    /// <summary>The verdict knows which pair it describes and when it was taken.</summary>
    /// <remarks>
    /// Both become primary-key material the moment Phase 6 persists these, and the timestamp is
    /// load-bearing besides: a verdict taken before a suite is not evidence about the path during
    /// it, and an RFC 2544 latency run alone is forty minutes.
    /// </remarks>
    [Fact]
    public async Task TheVerdictCarriesItsIdentityAndTimestamp()
    {
        var (rig, detector) = Build();
        CrossingSweep(rig);

        var verdict = await detector.DetectAsync(Request());

        Assert.Equal(TxId, verdict.TransmitAdapterId);
        Assert.Equal(RxId, verdict.ReceiveAdapterId);
        Assert.NotNull(verdict.MeasuredAt);
    }
}
