namespace EthLinkTester.Core.Adapters;

/// <summary>
/// A snapshot of an adapter's hardware counters.
/// </summary>
/// <remarks>
/// These come from the NIC itself, not from userspace capture. That distinction is the whole
/// reason this type exists: at rate a userspace capture drops frames, and a dropped capture is
/// indistinguishable from loss caused by the cable. The hardware count is exact and free.
/// </remarks>
public sealed record AdapterCounters
{
    public required string AdapterId { get; init; }

    /// <summary>When the snapshot was taken, from a monotonic source.</summary>
    public required long TimestampTicks { get; init; }

    public long ReceivedBytes { get; init; }

    public long ReceivedUnicastPackets { get; init; }

    /// <summary>Receive errors, which is where a marginal cable becomes visible.</summary>
    public long ReceivedPacketErrors { get; init; }

    public long ReceivedDiscardedPackets { get; init; }

    public long SentBytes { get; init; }

    public long SentUnicastPackets { get; init; }

    public long OutboundPacketErrors { get; init; }

    public long OutboundDiscardedPackets { get; init; }

    /// <summary>
    /// Difference between this snapshot and an earlier one, with rates over the interval.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the snapshots are from different adapters, or when
    /// <paramref name="earlier"/> is not actually earlier - a counter delta computed across a
    /// backwards interval would silently produce negative rates.
    /// </exception>
    public CounterDelta Since(AdapterCounters earlier, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(earlier);

        if (earlier.AdapterId != AdapterId)
        {
            throw new ArgumentException(
                $"Counter snapshots are from different adapters ({earlier.AdapterId} and {AdapterId}).",
                nameof(earlier));
        }

        if (earlier.TimestampTicks > TimestampTicks)
        {
            throw new ArgumentException(
                "The earlier snapshot has a later timestamp.", nameof(earlier));
        }

        var seconds = (TimestampTicks - earlier.TimestampTicks) / (double)timestampFrequency;

        return new CounterDelta
        {
            Seconds = seconds,
            ReceivedBytes = ReceivedBytes - earlier.ReceivedBytes,
            ReceivedPackets = ReceivedUnicastPackets - earlier.ReceivedUnicastPackets,
            ReceivedErrors = ReceivedPacketErrors - earlier.ReceivedPacketErrors,
            SentBytes = SentBytes - earlier.SentBytes,
            SentPackets = SentUnicastPackets - earlier.SentUnicastPackets,
            OutboundErrors = OutboundPacketErrors - earlier.OutboundPacketErrors,
        };
    }
}

/// <summary>Change in counters over an interval, with derived rates.</summary>
public sealed record CounterDelta
{
    public required double Seconds { get; init; }

    public long ReceivedBytes { get; init; }

    public long ReceivedPackets { get; init; }

    public long ReceivedErrors { get; init; }

    public long SentBytes { get; init; }

    public long SentPackets { get; init; }

    public long OutboundErrors { get; init; }

    public double ReceivedMegabitsPerSecond => Seconds <= 0 ? 0 : ReceivedBytes * 8 / 1_000_000.0 / Seconds;

    public double SentMegabitsPerSecond => Seconds <= 0 ? 0 : SentBytes * 8 / 1_000_000.0 / Seconds;

    /// <summary>
    /// Observed frame error ratio, or null when no frames arrived. Null rather than zero on
    /// purpose: "no errors in no traffic" is not evidence of a good link.
    /// </summary>
    public double? ReceivedErrorRatio =>
        ReceivedPackets <= 0 ? null : ReceivedErrors / (double)ReceivedPackets;
}
