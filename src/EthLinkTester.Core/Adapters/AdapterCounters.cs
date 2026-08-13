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

    /// <summary>
    /// Broadcast frames received. Counted separately by the NIC but included in
    /// <see cref="ReceivedBytes"/>.
    /// </summary>
    public long ReceivedBroadcastPackets { get; init; }

    /// <summary>
    /// Multicast frames received. Counted separately by the NIC but included in
    /// <see cref="ReceivedBytes"/>.
    /// </summary>
    public long ReceivedMulticastPackets { get; init; }

    /// <summary>Receive errors, which is where a marginal cable becomes visible.</summary>
    public long ReceivedPacketErrors { get; init; }

    public long ReceivedDiscardedPackets { get; init; }

    public long SentBytes { get; init; }

    public long SentUnicastPackets { get; init; }

    public long OutboundPacketErrors { get; init; }

    public long OutboundDiscardedPackets { get; init; }

    /// <summary>
    /// Every frame delivered intact, regardless of destination type.
    /// </summary>
    /// <remarks>
    /// The NIC counts unicast, broadcast, and multicast separately, but sums all three into
    /// <see cref="ReceivedBytes"/>. Using the unicast count alone as "frames received" therefore
    /// compares two different populations - and on a quiet link it is routinely zero while
    /// broadcast traffic flows, which is the exact state the reference rig idles in.
    /// </remarks>
    public long ReceivedFrames =>
        ReceivedUnicastPackets + ReceivedBroadcastPackets + ReceivedMulticastPackets;

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
            SpansCounterReset = HasResetSince(earlier),
            ReceivedBytes = ReceivedBytes - earlier.ReceivedBytes,
            ReceivedFrames = ReceivedFrames - earlier.ReceivedFrames,
            ReceivedErrors = ReceivedPacketErrors - earlier.ReceivedPacketErrors,
            SentBytes = SentBytes - earlier.SentBytes,
            SentPackets = SentUnicastPackets - earlier.SentUnicastPackets,
            OutboundErrors = OutboundPacketErrors - earlier.OutboundPacketErrors,
        };
    }

    /// <summary>
    /// True when any counter went backwards, which only happens when the NIC's counters were
    /// reset - a disable/enable cycle, a driver reload, or a surprise-removal of a USB adapter.
    /// </summary>
    /// <remarks>
    /// These counters are monotonic while the adapter stays up, so a decrease is unambiguous.
    /// Detecting it matters because the arithmetic stays perfectly plausible either way: the
    /// reference rig produced -0.315 Mbps and a -0.667 error ratio across a disable/enable,
    /// numbers a chart would happily plot and a grader would happily read.
    /// </remarks>
    private bool HasResetSince(AdapterCounters earlier) =>
        ReceivedBytes < earlier.ReceivedBytes
        || ReceivedFrames < earlier.ReceivedFrames
        || ReceivedPacketErrors < earlier.ReceivedPacketErrors
        || ReceivedDiscardedPackets < earlier.ReceivedDiscardedPackets
        || SentBytes < earlier.SentBytes
        || SentUnicastPackets < earlier.SentUnicastPackets
        || OutboundPacketErrors < earlier.OutboundPacketErrors
        || OutboundDiscardedPackets < earlier.OutboundDiscardedPackets;
}

/// <summary>Change in counters over an interval, with derived rates.</summary>
/// <remarks>
/// Every derived figure is nullable, and null always means "this interval cannot be measured"
/// rather than zero. Zero is a real and meaningful reading - a silent link - so conflating the
/// two would report a driver reload as a dead cable.
/// </remarks>
public sealed record CounterDelta
{
    public required double Seconds { get; init; }

    /// <summary>
    /// True when the NIC's counters reset during the interval, making every figure here
    /// meaningless. Callers should discard the sample and re-baseline.
    /// </summary>
    public bool SpansCounterReset { get; init; }

    public long ReceivedBytes { get; init; }

    /// <summary>Frames delivered intact: unicast, broadcast, and multicast together.</summary>
    public long ReceivedFrames { get; init; }

    public long ReceivedErrors { get; init; }

    public long SentBytes { get; init; }

    public long SentPackets { get; init; }

    public long OutboundErrors { get; init; }

    /// <summary>True when the interval is long enough and intact enough to derive rates from.</summary>
    private bool IsMeasurable => !SpansCounterReset && Seconds > 0;

    public double? ReceivedMegabitsPerSecond =>
        IsMeasurable ? ReceivedBytes * 8 / 1_000_000.0 / Seconds : null;

    public double? SentMegabitsPerSecond =>
        IsMeasurable ? SentBytes * 8 / 1_000_000.0 / Seconds : null;

    /// <summary>
    /// Observed frame error ratio, or null when nothing arrived at all.
    /// </summary>
    /// <remarks>
    /// The denominator is every frame the PHY saw - delivered plus errored - because a count of
    /// delivered frames excludes the errored ones by definition. Dividing by the successes alone
    /// understates the rate, and does so worst exactly when the link is worst: a link where every
    /// frame errors would divide by zero rather than report 100%.
    /// <para>
    /// "Delivered" must mean all three destination types. Counting unicast alone leaves the
    /// broadcast and multicast frames that a quiet link carries out of the denominator while
    /// their errors stay in the numerator, which reads as a totally failed link on a healthy one.
    /// </para>
    /// <para>
    /// Null rather than zero when nothing arrived. "No errors in no traffic" is not evidence of
    /// a good link.
    /// </para>
    /// </remarks>
    public double? ReceivedErrorRatio =>
        SpansCounterReset || ReceivedFrames + ReceivedErrors <= 0
            ? null
            : ReceivedErrors / (double)(ReceivedFrames + ReceivedErrors);
}
