using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// Runs the topology signals in order and combines what they found.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cheapest and safest first, and disruptive only if the free signals left the question open.</b>
/// The free comparison and the sweep cost milliseconds and change nothing. The forced-speed probe
/// drops the link for several seconds and needs a restore afterwards, so it runs last, only when
/// permitted, and only when it can still change the answer - a link already proven bridged at high
/// confidence does not need its speed forced to prove it again, and forcing it would risk the link
/// for no information.
/// </para>
/// <para>
/// <b>Every signal appears in the result, including the ones that did not run.</b> A report that
/// silently omits the tests it skipped is claiming coverage it does not have, and RFC 2544 section
/// 7 asks for the configuration actually used - what was disabled included. So a declined
/// disruptive probe, an unavailable driver setting and a listen nobody asked for all come back as
/// <see cref="TopologyObservation.NotRun"/> with the reason, rather than as absence.
/// </para>
/// <para>
/// <b>The restore is in a finally block and is scoped to the one property this forced.</b> A global
/// restore here would revert whatever else the surrounding run had configured; see
/// <see cref="RestoreScope"/>. If the restore itself fails the journal keeps the entry, so the next
/// launch puts it back - that is the safety net working, not a leak.
/// </para>
/// </remarks>
public sealed class TopologyDetector
{
    private readonly IAdapterProvider _adapters;
    private readonly IAdapterConfigurator _configurator;
    private readonly ITopologyProbe _probe;
    private readonly TimeProvider _time;

    /// <summary>How often to re-read the adapters while waiting for a link to settle.</summary>
    /// <remarks>
    /// A CIM read of every physical adapter costs tens of milliseconds, and a PHY takes seconds to
    /// negotiate, so polling faster than this buys nothing and polling much slower adds latency to
    /// a probe that is already the slow one.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public TopologyDetector(
        IAdapterProvider adapters,
        IAdapterConfigurator configurator,
        ITopologyProbe probe,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(probe);

        _adapters = adapters;
        _configurator = configurator;
        _probe = probe;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs the signals and returns the combined verdict.</summary>
    public async Task<TopologyVerdict> DetectAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = new List<TopologyObservation>();

        var (transmit, receive) = await ReadPairAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // Free, and the only signal that costs nothing at all - the numbers are already on screen.
        observations.Add(LinkSpeedSignal.Observe(transmit, receive));

        observations.Add(await SweepAsync(request, cancellationToken).ConfigureAwait(false));
        observations.Add(await ListenAsync(request, cancellationToken).ConfigureAwait(false));

        // Not viable on this rig and honest about why, rather than absent. The discrimination gap
        // is 8 ns/byte at 1 Gbps against 1-4 ns/byte of NPF copy cost that covaries with frame
        // size, which is a bias no sample count removes.
        observations.Add(TopologyObservation.NotRun(
            TopologySignal.LatencySlope,
            "Not run: at 1 Gbps the store-and-forward signal is 8 ns per byte and the capture "
            + "path's own per-byte cost is 1-4, which biases the slope toward reporting a switch. "
            + "It becomes usable at 100 Mbps, where the gap is fourteen times larger."));

        observations.Add(TopologyObservation.NotRun(
            TopologySignal.LinkStateCoupling,
            "Not run: taking one port administratively down and watching the other is not "
            + "implemented yet. The forced-speed probe tests the same coupling less brutally."));

        observations.Add(
            await ForcedSpeedAsync(request, observations, cancellationToken).ConfigureAwait(false));

        return TopologyVerdict.From(observations) with
        {
            TransmitAdapterId = request.TransmitAdapterId,
            ReceiveAdapterId = request.ReceiveAdapterId,
            MeasuredAt = _time.GetUtcNow(),
        };
    }

    private async Task<TopologyObservation> SweepAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _probe
                .SweepAsync(request.TransmitAdapterId, request.ReceiveAdapterId, cancellationToken)
                .ConfigureAwait(false);

            return ReservedMulticastProbe.Observe(results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sweep that could not run is not a sweep that found nothing, and the difference
            // matters: the first is a broken instrument and the second is evidence.
            return TopologyObservation.NotRun(
                TopologySignal.ReservedMulticastProbe,
                $"The multicast sweep could not run: {ex.Message}");
        }
    }

    private async Task<TopologyObservation> ListenAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken)
    {
        if (request.PassiveWindow <= TimeSpan.Zero)
        {
            return TopologyObservation.NotRun(
                TopologySignal.BridgeProtocolTraffic,
                "Not run: listening for LLDP, CDP and STP needs about three minutes to make "
                + "silence mean anything, and it can only ever prove a bridge - never a cable.");
        }

        try
        {
            var heard = await _probe
                .ListenAsync(
                    [request.TransmitAdapterId, request.ReceiveAdapterId],
                    request.PassiveWindow,
                    cancellationToken)
                .ConfigureAwait(false);

            return BridgeProtocolSignal.Observe(heard);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TopologyObservation.NotRun(
                TopologySignal.BridgeProtocolTraffic,
                $"The passive listen could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// Forces one end to 100 Mbps, reads both, and puts the setting back.
    /// </summary>
    private async Task<TopologyObservation> ForcedSpeedAsync(
        TopologyDetectionRequest request,
        IReadOnlyList<TopologyObservation> soFar,
        CancellationToken cancellationToken)
    {
        if (!request.AllowDisruptive)
        {
            return TopologyObservation.NotRun(
                TopologySignal.ForcedSpeedAsymmetry,
                "Not run: the disruptive tests were declined. This is the only signal that can "
                + "positively demonstrate a direct cable, so without it a healthy direct rig "
                + "reports an unestablished topology and results cannot be attributed to a cable.");
        }

        // Nothing to gain and a link to lose. A conclusive bridge from the free comparison already
        // settles the question at the highest confidence this model awards, and the forced probe
        // cannot raise it.
        if (soFar.Any(o => o.Finding == TopologyFinding.Bridged
                        && o.Strength == SignalStrength.Conclusive))
        {
            return TopologyObservation.NotRun(
                TopologySignal.ForcedSpeedAsymmetry,
                "Not run: the two ports already negotiated different speeds, which one cable "
                + "cannot do. Forcing a speed would bounce the link to re-prove a settled point.");
        }

        var (transmit, receive) = await ReadPairAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // The free end is the one carrying the default route if either does, so the forced one is
        // the adapter whose link going down costs least. On a bench rig neither carries it and this
        // falls through to forcing the transmit adapter.
        var (forced, free) = receive.CarriesDefaultRoute && !transmit.CarriesDefaultRoute
            ? (transmit, receive)
            : transmit.CarriesDefaultRoute && !receive.CarriesDefaultRoute
                ? (receive, transmit)
                : (transmit, receive);

        ConfigurationOutcome outcome;

        try
        {
            outcome = await _configurator
                .ForceSpeedAsync(forced, request.ForceTarget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The commonest cause by far, and not a defect: plenty of drivers offer no fixed
            // 100 Mbps setting at all, and one on this rig advertises a gigabit it does not honour.
            return TopologyObservation.NotRun(
                TopologySignal.ForcedSpeedAsymmetry,
                $"Not run: {forced.Name} would not take a forced {request.ForceTarget}. {ex.Message}");
        }

        try
        {
            await SettleAsync(request, forced.Id, request.ForceTarget.Speed, cancellationToken)
                .ConfigureAwait(false);

            var (transmitAfter, receiveAfter) = await ReadPairAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var forcedAfter = Same(transmitAfter, forced) ? transmitAfter : receiveAfter;
            var freeAfter = Same(transmitAfter, forced) ? receiveAfter : transmitAfter;

            return ForcedSpeedAsymmetrySignal.Observe(new ForcedSpeedProbe
            {
                ForcedBefore = forced,
                FreeBefore = free,
                ForcedAfter = forcedAfter,
                FreeAfter = freeAfter,
                Outcome = outcome,
                Target = request.ForceTarget.Speed,
            });
        }
        finally
        {
            // Scoped to the one property, so a suite's other settings survive. Not conditional on
            // success: the whole reason for the journal is that an unrestored adapter is worse than
            // a missing measurement, and an exception on the way through here is exactly when the
            // restore matters most.
            await _configurator.RestoreAsync(
                    RestoreScope.Property(forced.Id, WellKnownKeywords.SpeedDuplex),
                    CancellationToken.None)
                .ConfigureAwait(false);

            await SettleAsync(request, forced.Id, expected: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for an adapter's link to come back, and to reach <paramref name="expected"/> if given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns rather than throws on timeout, and that is deliberate. A link that does not come
    /// back is a real outcome the signal knows how to describe - a far end with no 100BASE-TX
    /// ability, or automatic MDI/MDI-X dropping out because it is driven by link pulses that a
    /// forced PHY no longer sends. Throwing here would turn a reportable observation into an error.
    /// </para>
    /// <para>
    /// Waiting for the expected speed rather than merely for a link matters on the way in: a driver
    /// that takes the write and keeps negotiating comes back up at a gigabit almost immediately, and
    /// returning on first link would hand the signal a reading taken before the PHY had settled -
    /// making an honest driver look like a dishonest one.
    /// </para>
    /// </remarks>
    private async Task SettleAsync(
        TopologyDetectionRequest request,
        string adapterId,
        LinkSpeed? expected,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + request.LinkSettleTimeout;

        while (true)
        {
            var adapter = await FindAsync(adapterId, cancellationToken).ConfigureAwait(false);

            // Null is expected while the miniport restarts: writing *SpeedDuplex takes the adapter
            // out of the enumeration entirely for a moment.
            if (adapter?.NegotiatedSpeed is { } speed && (expected is null || speed == expected))
            {
                return;
            }

            // Read first, then wait. A link that is already where it should be costs nothing, which
            // is what makes this loop cheap on a rig that settles fast and testable without a
            // controllable timer.
            if (_time.GetUtcNow() >= deadline)
            {
                return;
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(NetworkAdapterInfo Transmit, NetworkAdapterInfo Receive)> ReadPairAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken)
    {
        var all = await _adapters.GetPhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);

        return (
            Find(all, request.TransmitAdapterId),
            Find(all, request.ReceiveAdapterId));
    }

    private async Task<NetworkAdapterInfo?> FindAsync(
        string adapterId, CancellationToken cancellationToken)
    {
        var all = await _adapters.GetPhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);

        return all.FirstOrDefault(
            a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase));
    }

    private static NetworkAdapterInfo Find(
        IReadOnlyList<NetworkAdapterInfo> all, string adapterId) =>
        all.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase))
            ?? throw AdapterNotFoundException.ForAdapter(adapterId);

    private static bool Same(NetworkAdapterInfo a, NetworkAdapterInfo b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
}
