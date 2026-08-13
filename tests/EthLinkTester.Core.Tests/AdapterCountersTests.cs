using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class AdapterCountersTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    private static AdapterCounters Snapshot(
        long ticks,
        long rxPackets = 0,
        long rxErrors = 0,
        long rxBytes = 0,
        long rxBroadcast = 0,
        long rxMulticast = 0) => new()
        {
            AdapterId = "adapter",
            TimestampTicks = ticks,
            ReceivedUnicastPackets = rxPackets,
            ReceivedBroadcastPackets = rxBroadcast,
            ReceivedMulticastPackets = rxMulticast,
            ReceivedPacketErrors = rxErrors,
            ReceivedBytes = rxBytes,
        };

    [Fact]
    public void ComputesRatesOverTheInterval()
    {
        var delta = Snapshot(Frequency * 2, rxBytes: 250_000_000)
            .Since(Snapshot(0), Frequency);

        Assert.Equal(2, delta.Seconds, precision: 6);
        Assert.Equal(1000, delta.ReceivedMegabitsPerSecond!.Value, precision: 6);
    }

    /// <summary>
    /// Regression: the denominator is every frame the PHY saw, not just the intact ones.
    /// A delivered-frame count excludes errored frames by definition, so dividing by it alone
    /// understates the error rate - and understates it worst exactly when the link is worst.
    /// </summary>
    [Fact]
    public void ErrorRatioCountsErroredFramesInTheDenominator()
    {
        var delta = Snapshot(Frequency, rxPackets: 99, rxErrors: 1).Since(Snapshot(0), Frequency);

        // 1 of 100 frames seen, not 1 of 99 delivered.
        Assert.Equal(0.01, delta.ReceivedErrorRatio!.Value, precision: 10);
    }

    /// <summary>
    /// Regression, measured on the reference rig: an idle link carries broadcast and multicast
    /// traffic and no unicast at all. Counting only unicast frames as "delivered" left them out
    /// of the denominator while their errors stayed in the numerator, so three CRC errors
    /// alongside 1513 healthy broadcast frames reported a 100% error rate on a working cable.
    /// </summary>
    [Fact]
    public void ErrorRatioCountsBroadcastAndMulticastFrames()
    {
        var delta = Snapshot(Frequency, rxPackets: 0, rxBroadcast: 1328, rxMulticast: 185, rxErrors: 3)
            .Since(Snapshot(0), Frequency);

        Assert.Equal(3 / 1516.0, delta.ReceivedErrorRatio!.Value, precision: 10);
    }

    /// <summary>
    /// Regression: the same mismatch made a busy link look silent. ReceivedBytes includes
    /// broadcast and multicast, so bytes arrived while the unicast-only frame count stayed at
    /// zero and the ratio reported "no traffic".
    /// </summary>
    [Fact]
    public void NonUnicastTrafficIsNotMistakenForSilence()
    {
        var delta = Snapshot(Frequency, rxBroadcast: 40, rxBytes: 4260).Since(Snapshot(0), Frequency);

        Assert.NotNull(delta.ReceivedErrorRatio);
        Assert.Equal(0, delta.ReceivedErrorRatio!.Value, precision: 10);
        Assert.True(delta.ReceivedMegabitsPerSecond > 0);
    }

    /// <summary>
    /// The pathological case the old formula got most wrong: every frame errors, nothing is
    /// delivered. That is a 100% error rate, not an undefined one.
    /// </summary>
    [Fact]
    public void TotallyFailingLinkReportsFullErrorRatio()
    {
        var delta = Snapshot(Frequency, rxPackets: 0, rxErrors: 500).Since(Snapshot(0), Frequency);

        Assert.Equal(1.0, delta.ReceivedErrorRatio!.Value, precision: 10);
    }

    /// <summary>Silence is not a passing result.</summary>
    [Fact]
    public void NoTrafficAtAllHasNoErrorRatio()
    {
        var delta = Snapshot(Frequency).Since(Snapshot(0), Frequency);

        Assert.Null(delta.ReceivedErrorRatio);
    }

    [Fact]
    public void RejectsSnapshotsFromDifferentAdapters()
    {
        var other = Snapshot(0) with { AdapterId = "somebody-else" };

        Assert.Throws<ArgumentException>(() => Snapshot(Frequency).Since(other, Frequency));
    }

    /// <summary>
    /// A backwards interval would silently produce negative rates, which read as plausible
    /// numbers rather than as the corrupt measurement they are.
    /// </summary>
    [Fact]
    public void RejectsABackwardsInterval()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(0).Since(Snapshot(Frequency), Frequency));
    }

    /// <summary>
    /// Regression, measured across a real disable/enable on the reference rig: ReceivedBytes went
    /// 397233 -> 3146 and the delta reported -0.315 Mbps. Negative throughput is not a number any
    /// consumer should have to recognise as corrupt, so the interval reports itself unmeasurable.
    /// </summary>
    [Fact]
    public void CounterResetYieldsNoRatesRatherThanNegativeOnes()
    {
        var before = Snapshot(0, rxBytes: 397_233, rxPackets: 900);
        var after = Snapshot(Frequency, rxBytes: 3_146, rxPackets: 4);

        var delta = after.Since(before, Frequency);

        Assert.True(delta.SpansCounterReset);
        Assert.Null(delta.ReceivedMegabitsPerSecond);
        Assert.Null(delta.SentMegabitsPerSecond);
        Assert.Null(delta.ReceivedErrorRatio);
    }

    /// <summary>
    /// A partial reset is the nastier variant: errors reset while frames kept climbing, which
    /// produced a -0.667 error ratio rather than anything obviously wrong.
    /// </summary>
    [Fact]
    public void PartialCounterResetIsAlsoDetected()
    {
        var before = Snapshot(0, rxPackets: 100, rxErrors: 30);
        var after = Snapshot(Frequency, rxPackets: 130, rxErrors: 0);

        var delta = after.Since(before, Frequency);

        Assert.True(delta.SpansCounterReset);
        Assert.Null(delta.ReceivedErrorRatio);
    }

    /// <summary>An ordinary interval must not be mistaken for a reset.</summary>
    [Fact]
    public void MonotonicCountersDoNotLookLikeAReset()
    {
        var delta = Snapshot(Frequency, rxPackets: 500, rxBytes: 60_000)
            .Since(Snapshot(0, rxPackets: 100, rxBytes: 10_000), Frequency);

        Assert.False(delta.SpansCounterReset);
        Assert.NotNull(delta.ReceivedMegabitsPerSecond);
    }
}
