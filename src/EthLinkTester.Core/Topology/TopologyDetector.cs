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
    public async Task<TopologyDetection> DetectAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.PollInterval, TimeSpan.Zero);

        var observations = new List<TopologyObservation>();

        // Free, and the only signal that costs nothing at all - the numbers are already on screen.
        observations.Add(await CompareSpeedsAsync(request, cancellationToken).ConfigureAwait(false));

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

        var (forcedSpeed, restore) = await ForcedSpeedAsync(
            request, observations, cancellationToken).ConfigureAwait(false);
        observations.Add(forcedSpeed);

        var verdict = TopologyVerdict.From(observations) with
        {
            TransmitAdapterId = request.TransmitAdapterId,
            ReceiveAdapterId = request.ReceiveAdapterId,
            MeasuredAt = _time.GetUtcNow(),
        };

        return new TopologyDetection(verdict, restore);
    }

    /// <summary>
    /// Compares the two ports' speeds, confirming a mismatch before reporting one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two reads, because the first one is not evidence on its own.</b>
    /// <see cref="LinkSpeedSignal"/> calls a mismatch <see cref="SignalStrength.Conclusive"/> - the
    /// alternative is physically impossible, since one cable carries one link - and its own remarks
    /// name the gap: the physics is exact but the evidence is two CIM queries milliseconds apart,
    /// and a link retraining between them fabricates a mismatch on a bare cable. That prescription
    /// sat in the signal's documentation while the orchestrator read once and promoted straight to
    /// Conclusive.
    /// </para>
    /// <para>
    /// It compounds, which is what lifts it from a cosmetic worry: a Conclusive bridge suppresses
    /// the forced-speed probe - correctly, since bouncing a link to re-prove a settled point is
    /// pure risk - so the same unconfirmed reading also removes the one signal that could have
    /// refuted it. A downshift under thermal stress is the fault this tool exists to find, and it
    /// is exactly the transient that produces this.
    /// </para>
    /// <para>
    /// Agreement is not re-read. A matching pair is already Inconclusive and costs nothing to be
    /// wrong about, so the extra query is spent only where it changes an answer.
    /// </para>
    /// </remarks>
    private async Task<TopologyObservation> CompareSpeedsAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken)
    {
        var (transmit, receive) = await ReadPairAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var first = LinkSpeedSignal.Observe(transmit, receive);

        if (first.Finding != TopologyFinding.Bridged)
        {
            return first;
        }

        await Task.Delay(request.PollInterval, _time, cancellationToken).ConfigureAwait(false);

        var (transmitAgain, receiveAgain) = await ReadPairAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var second = LinkSpeedSignal.Observe(transmitAgain, receiveAgain);

        if (second.Finding == TopologyFinding.Bridged)
        {
            return second;
        }

        // A mismatch that did not survive a second look is a link that moved, not two links. Said
        // plainly rather than swallowed: a retrain during a measurement is itself worth knowing,
        // and it is the reason the disruptive probe is about to run after all.
        return TopologyObservation.Nothing(
            TopologySignal.LinkSpeedMismatch,
            "The two ports read different speeds and then agreed a moment later, so a link was "
            + "changing speed while they were read rather than there being two separate links. "
            + "A single reading is not evidence about a topology, and this one has been discarded.");
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
    private async Task<(TopologyObservation Observation, RestoreOutcome? Restore)> ForcedSpeedAsync(
        TopologyDetectionRequest request,
        IReadOnlyList<TopologyObservation> soFar,
        CancellationToken cancellationToken)
    {
        RestoreOutcome? restore = null;

        if (!request.AllowDisruptive)
        {
            return (TopologyObservation.NotRun(
                TopologySignal.ForcedSpeedAsymmetry,
                "Not run: the disruptive tests were declined. This is the only signal that can "
                + "positively demonstrate a direct cable, so without it a healthy direct rig "
                + "reports an unestablished topology and results cannot be attributed to a cable."),
                restore);
        }

        // Nothing to gain and a link to lose. A conclusive bridge from the free comparison already
        // settles the question at the highest confidence this model awards, and the forced probe
        // cannot raise it.
        if (soFar.Any(o => o.Finding == TopologyFinding.Bridged
                        && o.Strength == SignalStrength.Conclusive))
        {
            return (TopologyObservation.NotRun(
                TopologySignal.ForcedSpeedAsymmetry,
                "Not run: the two ports already negotiated different speeds, which one cable "
                + "cannot do. Forcing a speed would bounce the link to re-prove a settled point."),
                restore);
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

        // The observation is built into a local and returned *after* the cleanup, not returned from
        // inside the try. A `return` evaluates its expression before the finally runs, so returning
        // the tuple here would capture `restore` while it was still null and silently report every
        // failed restore as no restore at all - which a test caught, and reading would not have.
        TopologyObservation observation;

        // The force is inside the try, not before it. GuardedAdapterConfigurator records the
        // original value durably and *then* writes, so a write that throws leaves a journal entry
        // behind and an adapter that may or may not have changed - and a catch outside the finally
        // would return a harmless-looking "not run" while walking away from it. Restoring a scope
        // that was never journaled is a no-op, so there is no cost to always entering the cleanup.
        try
        {
            ConfigurationOutcome outcome;

            try
            {
                outcome = await _configurator
                    .ForceSpeedAsync(forced, request.ForceTarget, cancellationToken)
                    .ConfigureAwait(false);

                var (forcedAfter, freeAfter) = await SettleAsync(
                        request, forced.Id, free.Id, request.ForceTarget.Speed, cancellationToken)
                    .ConfigureAwait(false);

                observation = ForcedSpeedAsymmetrySignal.Observe(new ForcedSpeedProbe
                {
                    ForcedBefore = forced,
                    FreeBefore = free,
                    ForcedAfter = forcedAfter,
                    FreeAfter = freeAfter,
                    Outcome = outcome,
                    Target = request.ForceTarget.Speed,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The commonest cause by far, and not a defect: plenty of drivers offer no fixed
                // 100 Mbps setting at all, and one on this rig advertises a gigabit it does not
                // honour.
                observation = TopologyObservation.NotRun(
                    TopologySignal.ForcedSpeedAsymmetry,
                    $"Not run: {forced.Name} would not take a forced {request.ForceTarget}. "
                    + ex.Message);
            }
        }
        finally
        {
            // Scoped to the one property, so a suite's other settings survive. Not conditional on
            // success: the whole reason for the journal is that an unrestored adapter is worse than
            // a missing measurement, and an exception on the way through here is exactly when the
            // restore matters most.
            //
            // The outcome is kept rather than discarded. RestoreAsync reports a refused write in
            // Failures rather than throwing, so dropping it would let detection finish, the UI show
            // a verdict, and the adapter stay pinned at 100 Mbps with nobody told - recoverable on
            // the next launch, which is not the same as harmless when the port is someone's
            // network.
            restore = await _configurator.RestoreAsync(
                    RestoreScope.Property(forced.Id, WellKnownKeywords.SpeedDuplex),
                    CancellationToken.None)
                .ConfigureAwait(false);

            await SettleAsync(request, forced.Id, free.Id, expected: null, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return (observation, restore);
    }

    /// <summary>
    /// Waits for both ends to come back after a speed write, and returns them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both ends, and the free one twice.</b> Waiting only for the forced adapter is what the
    /// first version did, and on the reference rig it produced a confident "the far end did not
    /// link" about a cable that was linked the whole time: writing <c>*SpeedDuplex</c> restarts the
    /// miniport and bounces the link at <i>both</i> ends, so a pair read taken the moment the forced
    /// end reports its target catches the other one still down. Watching a bench meter showed both
    /// ends back at 100 Mbps within a second and a half; the detector said one of them was dark.
    /// </para>
    /// <para>
    /// The free end has to report the same speed on two consecutive polls, which narrows the
    /// opposite hazard: a stale reading taken before the restart shows the <i>old</i> speed and
    /// looks perfectly healthy, so "has a link" alone would proceed on a pre-force value. It
    /// narrows rather than closes it - two consecutive stale reads would still satisfy the rule -
    /// and what makes that unlikely in practice is the conjunction: the loop returns only when the
    /// forced end has also reached its target, which means the miniport restart has completed. A
    /// reading confirmed against a completed restart is a different thing from a reading confirmed
    /// against a clock.
    /// </para>
    /// <para>
    /// Waiting for the forced end to reach the <i>expected</i> speed rather than merely to link
    /// matters for a different reason: a driver that takes the write and keeps negotiating comes
    /// back at a gigabit almost immediately, and returning on first link would hand the signal a
    /// reading taken before the PHY had settled - making an honest driver look like a dishonest one.
    /// </para>
    /// <para>
    /// Returns whatever it has on timeout rather than throwing. A link that genuinely does not come
    /// back is a real outcome the signal knows how to describe, and throwing would turn a reportable
    /// observation into an error.
    /// </para>
    /// </remarks>
    private async Task<(NetworkAdapterInfo Forced, NetworkAdapterInfo Free)> SettleAsync(
        TopologyDetectionRequest request,
        string forcedId,
        string freeId,
        LinkSpeed? expected,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + request.LinkSettleTimeout;
        LinkSpeed? previousFree = null;
        (NetworkAdapterInfo? Forced, NetworkAdapterInfo? Free) latest = (null, null);

        while (true)
        {
            var all = await _adapters.GetPhysicalAdaptersAsync(cancellationToken)
                .ConfigureAwait(false);

            // Null is expected while the miniport restarts: writing *SpeedDuplex takes the adapter
            // out of the enumeration entirely for a moment.
            var forced = Match(all, forcedId);
            var free = Match(all, freeId);
            latest = (forced ?? latest.Forced, free ?? latest.Free);

            var forcedSettled = forced?.NegotiatedSpeed is { } speed
                && (expected is null || speed == expected);
            var freeSettled = free?.NegotiatedSpeed is { } freeSpeed && freeSpeed == previousFree;

            previousFree = free?.NegotiatedSpeed;

            if (forcedSettled && freeSettled)
            {
                break;
            }

            // Never waits past the deadline. A poll interval longer than the timeout would
            // otherwise wait the whole interval before noticing it had expired - a one-second
            // timeout waiting thirty - and the restore path runs this with cancellation
            // deliberately disabled, so an overshoot there is both unbounded and uninterruptible.
            var remaining = deadline - _time.GetUtcNow();

            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var wait = remaining < request.PollInterval ? remaining : request.PollInterval;

            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }

        // A pair that never came back at all is still a pair: the signal reads a null negotiated
        // speed as "did not link", which is the honest description of what was seen.
        return (
            latest.Forced ?? Down(forcedId),
            latest.Free ?? Down(freeId));
    }

    /// <summary>A placeholder for an adapter that vanished from the enumeration and stayed away.</summary>
    private static NetworkAdapterInfo Down(string adapterId) => new()
    {
        Id = adapterId,
        Name = adapterId,
        Description = "not currently enumerated",
        MacAddress = string.Empty,
        Status = AdapterStatus.Disconnected,
    };

    private async Task<(NetworkAdapterInfo Transmit, NetworkAdapterInfo Receive)> ReadPairAsync(
        TopologyDetectionRequest request, CancellationToken cancellationToken)
    {
        var all = await _adapters.GetPhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);

        return (
            Find(all, request.TransmitAdapterId),
            Find(all, request.ReceiveAdapterId));
    }

    private static NetworkAdapterInfo? Match(
        IReadOnlyList<NetworkAdapterInfo> all, string adapterId) =>
        all.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase));

    private static NetworkAdapterInfo Find(
        IReadOnlyList<NetworkAdapterInfo> all, string adapterId) =>
        Match(all, adapterId) ?? throw AdapterNotFoundException.ForAdapter(adapterId);
}
