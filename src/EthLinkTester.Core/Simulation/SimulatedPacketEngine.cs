using EthLinkTester.Core.Engine;

namespace EthLinkTester.Core.Simulation;

/// <summary>
/// The link behaviour a simulated run should imitate.
/// </summary>
/// <remarks>
/// These exist so the UI can be developed against the fault signatures the app is meant to
/// detect, not just against a healthy link. A chart that only ever shows a good cable teaches
/// you nothing about whether the app makes a bad one visible.
/// </remarks>
public enum SimulationProfile
{
    /// <summary>A good cable well inside spec.</summary>
    Healthy,

    /// <summary>
    /// The case this whole project exists for. Throughput still looks broadly fine because the
    /// PHY error-corrects, but tail latency blows out and errors trickle in. Judging this cable
    /// on throughput alone would pass it.
    /// </summary>
    Marginal,

    /// <summary>Obviously bad: throughput collapses and errors are continuous.</summary>
    Failing,
}

/// <summary>
/// Synthesises telemetry so the entire UI runs with no NICs, no cable, and no Npcap installed.
/// </summary>
/// <remarks>
/// <para>
/// Samples are generated on demand inside <see cref="Drain"/> from elapsed time rather than by a
/// background thread. There is nothing to start, stop, or dispose, and no thread-safety surface
/// invented purely for a stand-in. The real engine owns the threading; the mock only has to
/// honour the same contract.
/// </para>
/// <para>
/// Not thread-safe, which matches the single-producer/single-consumer contract the native ring
/// buffer imposes anyway.
/// </para>
/// </remarks>
public sealed class SimulatedPacketEngine : IPacketEngine
{
    private const int SampleRateHz = 60;

    /// <summary>
    /// Caps how far behind the consumer may fall. A real ring buffer overwrites rather than
    /// growing without bound, so a consumer that stalls loses the oldest samples instead of
    /// getting a burst of stale ones on the next poll.
    /// </summary>
    private const int MaxBacklogSamples = SampleRateHz * 2;

    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly SimulationProfile _profile;

    private EngineRunSettings? _settings;
    private long _lastSampleTimestamp;
    private long _txFrames;
    private long _rxFrames;
    private long _rxErrors;
    private double _errorRemainder;

    /// <param name="seed">
    /// Omit for run-to-run variation. Tests pass a fixed value to make a run reproducible;
    /// defaulting to a constant would make every run in the app replay an identical curve,
    /// which reads as a canned recording rather than a jitter model.
    /// </param>
    public SimulatedPacketEngine(
        SimulationProfile profile = SimulationProfile.Healthy,
        TimeProvider? timeProvider = null,
        int? seed = null)
    {
        _profile = profile;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = seed is null ? new Random() : new Random(seed.Value);
    }

    public bool IsSimulated => true;

    public EngineState State { get; private set; } = EngineState.Idle;

    /// <summary>Always null: a simulated run has nothing to go wrong with.</summary>
    public string? FaultDescription => null;

    public SimulationProfile Profile => _profile;

    public ValueTask StartAsync(EngineRunSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        _settings = settings;
        _lastSampleTimestamp = _timeProvider.GetTimestamp();
        _txFrames = 0;
        _rxFrames = 0;
        _rxErrors = 0;
        _errorRemainder = 0;
        // Reset with the rest. DroppedSamples is per-run, so carrying it over made a fresh
        // experiment open by reporting telemetry gaps that belonged to the previous one.
        _droppedSamples = 0;
        State = EngineState.Running;

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = EngineState.Idle;
        return ValueTask.CompletedTask;
    }

    private long _droppedSamples;

    /// <summary>
    /// Samples discarded because the consumer fell behind. The simulator models this because the
    /// native engine's ring genuinely overwrites, and a consumer that only ever meets a
    /// well-behaved producer will not have handled the case when it meets a real one.
    /// </summary>
    public long DroppedSamples => _droppedSamples;

    public int Drain(Span<TelemetrySample> destination)
    {
        if (State != EngineState.Running || _settings is null || destination.IsEmpty)
        {
            return 0;
        }

        var interval = 1.0 / SampleRateHz;

        // Monotonic, not wall clock. GetUtcNow moves backwards when NTP corrects the system
        // clock or a VM resumes, which would stall telemetry until real time caught up while
        // the engine still reported Running - indistinguishable from a dead link.
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastSampleTimestamp, now);
        var due = (int)(elapsed.TotalSeconds * SampleRateHz);

        if (due <= 0)
        {
            return 0;
        }

        var timestampPerSample = (long)(interval * _timeProvider.TimestampFrequency);

        // The consumer fell too far behind, so drop the backlog the way a ring buffer would
        // rather than replaying a burst of stale samples. Two things must both happen: the
        // dropped window is ACCRUED into the cumulative counters (they are documented as
        // running hardware totals, so skipping them would under-report for the rest of the
        // run), and the clock anchor is advanced past it (otherwise the next Drain sees the
        // same backlog again and the "drop" only defers the work).
        if (due > MaxBacklogSamples)
        {
            var dropped = due - MaxBacklogSamples;
            AccrueCounters(dropped, interval);
            _lastSampleTimestamp += dropped * timestampPerSample;
            _droppedSamples += dropped;
            due = MaxBacklogSamples;
        }

        var count = Math.Min(due, destination.Length);
        var ticksPerSample = (long)(interval * TimeSpan.TicksPerSecond);
        var wallClock = _timeProvider.GetUtcNow();

        for (var i = 0; i < count; i++)
        {
            _lastSampleTimestamp += timestampPerSample;
            destination[i] = NextSample(wallClock.AddTicks(ticksPerSample * i), interval);
        }

        return count;
    }

    public ValueTask DisposeAsync()
    {
        State = EngineState.Idle;
        return ValueTask.CompletedTask;
    }

    private TelemetrySample NextSample(DateTimeOffset at, double intervalSeconds)
    {
        var settings = _settings!;
        var shape = ShapeFor(_profile);
        var lineRateMbps = (double)settings.LinkSpeed.MegabitsPerSecond();

        var txMbps = Jitter(lineRateMbps * shape.ThroughputFraction, shape.ThroughputJitter);
        // Receive tracks transmit, because that is what the far end of a healthy link does. The
        // jitter is halved: the receiver counts whole frames the sender already paid the
        // variability for, so its figure is the smoother of the two on real hardware too.
        var rxMbps = Jitter(txMbps, shape.ThroughputJitter / 2);

        // Receives are derived from transmits minus modelled loss, never accumulated from the
        // jittered receive rate. Accumulating them independently let the two counters drift apart
        // by far more than the errors being modelled - so RxFrames routinely finished *above*
        // TxFrames, and the documented "loss is TxFrames minus RxFrames" then read as negative
        // loss and better than 100% delivery, on the Failing profile. The jitter belongs to the
        // rate on the chart, which is a display of an instant; the counters are the ledger.
        var sent = FramesFor(txMbps, intervalSeconds);
        _txFrames += sent;
        _rxFrames += Math.Max(0, sent - AccrueErrors(shape.ErrorsPerSecond * intervalSeconds));

        return new TelemetrySample
        {
            TimestampTicks = at.UtcTicks,
            TxMegabitsPerSecond = txMbps,
            RxMegabitsPerSecond = rxMbps,
            LatencyP50Microseconds = Jitter(shape.LatencyP50Microseconds, 0.15),
            LatencyP99Microseconds = Jitter(shape.LatencyP99Microseconds, 0.30),
            TxFrames = _txFrames,
            // Already net of loss - see where _rxFrames is accumulated. Loss caused by a cable
            // shows up in no receive counter, because the frame never arrives to be counted, so it
            // exists solely as this gap between what was sent and what was received. That is how
            // the real engine surfaces it and what Phase 6's grading will read.
            RxFrames = _rxFrames,
            // Zero, not `_rxErrors`. That field is the profile's *cable* error model, and
            // RxCaptureDrops means frames the host's own capture buffer lost - a property of how
            // busy this machine is, which is why it rises on a fast link with a perfect cable and
            // stays at zero on a broken one. Publishing cable errors through it taught the UI the
            // wrong fault signature: a Marginal profile rendered as host-side capture overload,
            // and the tile that exists to say "this measurement may be incomplete" said "this
            // cable is bad".
            //
            // The simulated host keeps up, so this is genuinely zero. The cable errors have
            // nowhere to go: TelemetrySample is pinned at 64 bytes and carries no field for them,
            // because the engine cannot see them either - loss caused by a cable appears in no
            // receive counter and has to be derived from TxFrames minus RxFrames. Grading in
            // Phase 6 is what needs them, and it will need somewhere to put them.
            RxCaptureDrops = 0,
        };
    }

    /// <summary>
    /// Advances the cumulative counters across a window of samples that were never
    /// materialised because the consumer stalled.
    /// </summary>
    /// <remarks>
    /// Frames and errors kept accruing on the wire while nobody was looking; only the
    /// per-sample detail is genuinely lost. Skipping the accrual would make the counters
    /// under-report for the remainder of the run, and <see cref="TelemetrySample"/> documents
    /// them as running hardware totals. Mean rates are used rather than jittered ones - the
    /// per-sample noise is exactly the part that no longer exists.
    /// </remarks>
    private void AccrueCounters(int sampleCount, double intervalSeconds)
    {
        if (sampleCount <= 0)
        {
            return;
        }

        var settings = _settings!;
        var shape = ShapeFor(_profile);
        var seconds = sampleCount * intervalSeconds;
        var meanMbps = settings.LinkSpeed.MegabitsPerSecond() * shape.ThroughputFraction;

        var sent = FramesFor(meanMbps, seconds);
        _txFrames += sent;
        _rxFrames += Math.Max(0, sent - AccrueErrors(shape.ErrorsPerSecond * seconds));
    }

    /// <summary>Frames carried at <paramref name="megabitsPerSecond"/> over a span, from the on-wire frame size.</summary>
    /// <remarks>
    /// The wire cost comes from <see cref="EthernetFrame"/> rather than a local constant. Both
    /// engines used to define it for themselves and disagreed by 29% at 64-byte frames, which made
    /// the same cable grade differently depending on which one measured it.
    /// </remarks>
    private long FramesFor(double megabitsPerSecond, double seconds)
    {
        var wireBits = EthernetFrame.WireBytes(_settings!.FrameBytes) * 8.0;
        return (long)(megabitsPerSecond * 1_000_000 / wireBits * seconds);
    }

    /// <summary>
    /// Carries the fractional part forward so an error rate below one per sample still yields
    /// occasional whole errors instead of flooring to zero forever.
    /// </summary>
    /// <returns>Whole errors accrued by this call, which is what the caller must not deliver.</returns>
    private long AccrueErrors(double errors)
    {
        _errorRemainder += errors;
        var whole = (long)_errorRemainder;
        _errorRemainder -= whole;
        _rxErrors += whole;
        return whole;
    }

    /// <summary>Applies +/- <paramref name="fraction"/> uniform noise, clamped at zero.</summary>
    private double Jitter(double value, double fraction) =>
        Math.Max(0, value * (1 + ((_random.NextDouble() * 2 - 1) * fraction)));

    private readonly record struct ProfileShape(
        double ThroughputFraction,
        double ThroughputJitter,
        double LatencyP50Microseconds,
        double LatencyP99Microseconds,
        double ErrorsPerSecond);

    /// <summary>
    /// Profiles as a table. Note the Marginal row: throughput is only mildly down while p99
    /// latency is five times Healthy and errors are non-zero. That asymmetry is the point - it
    /// is what makes a throughput-only verdict wrong.
    /// </summary>
    private static ProfileShape ShapeFor(SimulationProfile profile) => profile switch
    {
        //                              tput   jitter   p50    p99   err/s
        SimulationProfile.Healthy => new(0.94, 0.01, 120, 180, 0),
        SimulationProfile.Marginal => new(0.88, 0.06, 140, 900, 2),
        SimulationProfile.Failing => new(0.55, 0.20, 300, 4_000, 250),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };
}
