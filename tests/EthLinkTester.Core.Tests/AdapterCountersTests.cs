using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Tests;

public class AdapterCountersTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    private static AdapterCounters Snapshot(
        long ticks, long rxPackets = 0, long rxErrors = 0, long rxBytes = 0) => new()
        {
            AdapterId = "adapter",
            TimestampTicks = ticks,
            ReceivedUnicastPackets = rxPackets,
            ReceivedPacketErrors = rxErrors,
            ReceivedBytes = rxBytes,
        };

    [Fact]
    public void ComputesRatesOverTheInterval()
    {
        var delta = Snapshot(Frequency * 2, rxBytes: 250_000_000)
            .Since(Snapshot(0), Frequency);

        Assert.Equal(2, delta.Seconds, precision: 6);
        Assert.Equal(1000, delta.ReceivedMegabitsPerSecond, precision: 6);
    }

    /// <summary>
    /// Regression: the denominator is every frame the PHY saw, not just the intact ones.
    /// ReceivedUnicastPackets counts successes only, so dividing by it alone understates the
    /// error rate - and understates it worst exactly when the link is worst.
    /// </summary>
    [Fact]
    public void ErrorRatioCountsErroredFramesInTheDenominator()
    {
        var delta = Snapshot(Frequency, rxPackets: 99, rxErrors: 1).Since(Snapshot(0), Frequency);

        // 1 of 100 frames seen, not 1 of 99 delivered.
        Assert.Equal(0.01, delta.ReceivedErrorRatio!.Value, precision: 10);
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
}
