namespace EthLinkTester.Core.Engine;

/// <summary>
/// One instant of engine telemetry.
/// </summary>
/// <remarks>
/// Deliberately a fixed-size value type containing no references. The native engine writes
/// these into an unmanaged single-producer/single-consumer ring buffer that managed code reads
/// through a <see cref="Span{T}"/> over the raw pointer, so the layout has to stay blittable
/// and the managed side must allocate nothing per sample.
/// <para>
/// Latency is carried as percentiles rather than a mean on purpose. A marginal cable shows up
/// as tail latency; averaging hides exactly the signal worth having.
/// </para>
/// </remarks>
public readonly record struct TelemetrySample
{
    public required long TimestampTicks { get; init; }

    public required double TxMegabitsPerSecond { get; init; }

    public required double RxMegabitsPerSecond { get; init; }

    public required double LatencyP50Microseconds { get; init; }

    public required double LatencyP99Microseconds { get; init; }

    /// <summary>Cumulative frames transmitted since the run started.</summary>
    public required long TxFrames { get; init; }

    /// <summary>Cumulative frames received since the run started.</summary>
    public required long RxFrames { get; init; }

    /// <summary>
    /// Cumulative receive errors. Sourced from NIC hardware counters rather than userspace
    /// capture, because at rate a capture drops frames and that is indistinguishable from
    /// loss caused by the cable.
    /// </summary>
    public required long RxErrors { get; init; }

    public DateTimeOffset Timestamp => new(TimestampTicks, TimeSpan.Zero);
}
