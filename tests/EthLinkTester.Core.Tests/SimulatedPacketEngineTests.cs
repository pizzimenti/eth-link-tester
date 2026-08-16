using EthLinkTester.Core;
using EthLinkTester.Core.Engine;
using EthLinkTester.Core.Simulation;

namespace EthLinkTester.Core.Tests;

public class SimulatedPacketEngineTests
{
    private const int SampleRateHz = 60;

    private static EngineRunSettings GigabitRun() => new() { LinkSpeed = LinkSpeed.Mbps1000 };

    /// <summary>Fixed seed so a failure reproduces exactly rather than intermittently.</summary>
    private const int Seed = 20260812;

    private static async Task<(SimulatedPacketEngine Engine, TestClock Clock)> RunningEngineAsync(
        SimulationProfile profile = SimulationProfile.Healthy)
    {
        var clock = new TestClock();
        var engine = new SimulatedPacketEngine(profile, clock, Seed);
        await engine.StartAsync(GigabitRun());
        return (engine, clock);
    }

    private static async Task<List<TelemetrySample>> CollectAsync(
        SimulationProfile profile, TimeSpan duration)
    {
        var (engine, clock) = await RunningEngineAsync(profile);
        clock.Advance(duration);
        return Collect(engine);
    }

    /// <summary>Drains everything currently pending.</summary>
    private static List<TelemetrySample> Collect(SimulatedPacketEngine engine)
    {
        var samples = new List<TelemetrySample>();
        var buffer = new TelemetrySample[SampleRateHz * 4];

        int written;
        while ((written = engine.Drain(buffer)) > 0)
        {
            samples.AddRange(buffer[..written]);
        }

        return samples;
    }

    [Fact]
    public void Drain_ReturnsNothing_WhenIdle()
    {
        var engine = new SimulatedPacketEngine(timeProvider: new TestClock());
        Assert.Equal(EngineState.Idle, engine.State);
        Assert.Equal(0, engine.Drain(new TelemetrySample[16]));
    }

    [Fact]
    public async Task Drain_ReturnsNothing_WhenNoTimeHasPassed()
    {
        var (engine, _) = await RunningEngineAsync();
        Assert.Equal(0, engine.Drain(new TelemetrySample[16]));
    }

    [Fact]
    public async Task Drain_ProducesSamplesAtTheNominalRate()
    {
        var (engine, clock) = await RunningEngineAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SampleRateHz, engine.Drain(new TelemetrySample[SampleRateHz * 4]));
    }

    [Fact]
    public async Task Drain_FillsOnlyTheSpaceOffered()
    {
        var (engine, clock) = await RunningEngineAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        var buffer = new TelemetrySample[10];
        Assert.Equal(10, engine.Drain(buffer));
    }

    [Fact]
    public async Task Drain_DropsBacklog_RatherThanReplayingStaleSamples()
    {
        var (engine, clock) = await RunningEngineAsync();

        // A consumer that stalls for ten seconds must not receive ten seconds of history;
        // a real ring buffer would have overwritten it.
        clock.Advance(TimeSpan.FromSeconds(10));

        var total = 0;
        var buffer = new TelemetrySample[SampleRateHz * 4];
        int written;
        while ((written = engine.Drain(buffer)) > 0)
        {
            total += written;
        }

        Assert.Equal(SampleRateHz * 2, total);
    }

    /// <summary>
    /// Regression: elapsed time must come from a monotonic source. Measuring it from the wall
    /// clock stalled the engine for the length of any backward step while State stayed Running,
    /// which on screen is indistinguishable from a dead link.
    /// </summary>
    [Fact]
    public async Task BackwardWallClockStep_DoesNotStallTelemetry()
    {
        var (engine, clock) = await RunningEngineAsync();

        clock.StepWallClock(TimeSpan.FromSeconds(-30));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SampleRateHz, engine.Drain(new TelemetrySample[SampleRateHz * 4]));
    }

    /// <summary>
    /// Regression: dropping a backlog must not drop the cumulative counters with it. Frames and
    /// errors kept happening on the wire while the consumer was stalled, and TelemetrySample
    /// documents these as running hardware totals.
    /// </summary>
    [Fact]
    public async Task DroppedBacklog_StillAccruesCumulativeCounters()
    {
        const int seconds = 11;

        // Measured on RxFrames rather than RxCaptureDrops. This test used the capture-drop field
        // back when the simulation published its cable-error model through it; that field is now
        // what it says it is - frames the host's capture buffer lost - and the simulated host
        // never falls behind, so it is always zero and the comparison would have been 0 >= 0.
        // A vacuous assertion is worse than none, because it still reads as coverage.
        var (stalled, stalledClock) = await RunningEngineAsync(SimulationProfile.Failing);
        stalledClock.Advance(TimeSpan.FromSeconds(seconds));
        var stalledFrames = Collect(stalled)[^1].RxFrames;

        // The same elapsed time, polled once a second so nothing is ever dropped.
        var (polled, polledClock) = await RunningEngineAsync(SimulationProfile.Failing);
        var polledFrames = 0L;
        for (var i = 0; i < seconds; i++)
        {
            polledClock.Advance(TimeSpan.FromSeconds(1));
            polledFrames = Collect(polled)[^1].RxFrames;
        }

        Assert.True(
            stalledFrames >= polledFrames * 0.9,
            $"stalled run reported {stalledFrames} frames vs {polledFrames} when polled continuously");
    }

    [Fact]
    public async Task CumulativeCountersNeverDecrease()
    {
        var samples = await CollectAsync(SimulationProfile.Failing, TimeSpan.FromSeconds(1));

        Assert.NotEmpty(samples);
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].TxFrames >= samples[i - 1].TxFrames);
            Assert.True(samples[i].RxFrames >= samples[i - 1].RxFrames);
            Assert.True(samples[i].RxCaptureDrops >= samples[i - 1].RxCaptureDrops);
        }
    }

    /// <summary>
    /// A healthy cable loses nothing, and the host keeps up.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they are different measurements. These tests used to assert the
    /// profile's error model through <c>RxCaptureDrops</c>, which is host capture-buffer loss -
    /// a property of how busy this machine is, not of the cable. Cable loss has no counter of its
    /// own anywhere in the system; it exists only as sent minus received.
    /// </remarks>
    [Fact]
    public async Task HealthyProfileLosesNoFrames()
    {
        var samples = await CollectAsync(SimulationProfile.Healthy, TimeSpan.FromSeconds(1));

        Assert.NotEmpty(samples);

        // Close to transmit rather than equal to it. Receive is jittered independently of
        // transmit, so the two differ by a percent or so in either direction on a perfect link -
        // which is true of the real rig as well, where a window boundary falls between the two
        // counters. What a healthy profile must not show is a systematic deficit.
        Assert.InRange(
            samples[^1].RxFrames,
            (long)(samples[^1].TxFrames * 0.9),
            long.MaxValue);
        Assert.Equal(0, samples[^1].RxCaptureDrops);
    }

    /// <summary>
    /// Received never exceeds transmitted, on any profile.
    /// </summary>
    /// <remarks>
    /// Loss is documented as transmitted minus received, so this is what stops that arithmetic
    /// going negative and reporting better than 100% delivery. It did: receives used to be
    /// accumulated from an independently jittered receive rate, which drifted above transmits by
    /// far more than the errors being modelled - worst on the Failing profile, where the number
    /// matters most. Unseeded on purpose, because the jitter is where the defect lived and a fixed
    /// seed only ever exercises one path through it.
    /// </remarks>
    [Theory]
    [InlineData(SimulationProfile.Healthy)]
    [InlineData(SimulationProfile.Marginal)]
    [InlineData(SimulationProfile.Failing)]
    public async Task ReceivedNeverExceedsTransmitted(SimulationProfile profile)
    {
        var clock = new TestClock();
        var engine = new SimulatedPacketEngine(profile, clock);
        await engine.StartAsync(GigabitRun());

        for (var second = 0; second < 20; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var sample in Collect(engine))
            {
                Assert.True(
                    sample.RxFrames <= sample.TxFrames,
                    $"{profile}: received {sample.RxFrames} of {sample.TxFrames} sent, which is "
                    + "negative loss");
            }
        }
    }

    [Fact]
    public async Task FailingProfileLosesFrames()
    {
        var samples = await CollectAsync(SimulationProfile.Failing, TimeSpan.FromSeconds(1));

        Assert.True(
            samples[^1].TxFrames > samples[^1].RxFrames,
            $"sent {samples[^1].TxFrames} and received {samples[^1].RxFrames}; a failing cable "
            + "must lose frames");

        // The host is not the problem here, and the UI must not be taught that it is.
        Assert.Equal(0, samples[^1].RxCaptureDrops);
    }

    /// <summary>
    /// The central premise of the project, asserted on the simulator: a marginal cable's
    /// throughput stays close to healthy while its tail latency blows out. If this ever stops
    /// holding, the simulator has stopped modelling the fault the app exists to catch.
    /// </summary>
    [Fact]
    public async Task MarginalProfileHidesInThroughputButShowsInTailLatency()
    {
        var healthy = await CollectAsync(SimulationProfile.Healthy, TimeSpan.FromSeconds(1));
        var marginal = await CollectAsync(SimulationProfile.Marginal, TimeSpan.FromSeconds(1));

        var healthyThroughput = healthy.Average(s => s.TxMegabitsPerSecond);
        var marginalThroughput = marginal.Average(s => s.TxMegabitsPerSecond);
        var healthyTail = healthy.Average(s => s.LatencyP99Microseconds);
        var marginalTail = marginal.Average(s => s.LatencyP99Microseconds);

        // Throughput is within 15% - a throughput-only verdict would pass this cable.
        Assert.True(marginalThroughput > healthyThroughput * 0.85,
            $"marginal {marginalThroughput:n0} vs healthy {healthyThroughput:n0} Mbps");

        // Tail latency is several times worse, which is where the fault is actually visible.
        Assert.True(marginalTail > healthyTail * 3,
            $"marginal p99 {marginalTail:n0} vs healthy p99 {healthyTail:n0} us");
    }

    [Fact]
    public async Task StopAsync_HaltsProduction()
    {
        var (engine, clock) = await RunningEngineAsync();
        await engine.StopAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(EngineState.Idle, engine.State);
        Assert.Equal(0, engine.Drain(new TelemetrySample[SampleRateHz * 4]));
    }

    /// <summary>
    /// A run reports receive throughput that tracks transmit, which is what the far end of a
    /// healthy link actually does.
    /// </summary>
    /// <remarks>
    /// This replaces a test asserting the opposite. A `Bidirectional = false` run used to be
    /// modelled as receiving nothing, which described no real configuration: one end transmitting
    /// and the other receiving all of it is exactly what every hardware run does - 833 Mbps out
    /// and 829 in, measured. The simulation was teaching the UI a shape the engine never produces.
    /// </remarks>
    [Fact]
    public async Task ReceiveThroughputTracksTransmit()
    {
        var (engine, clock) = await RunningEngineAsync();
        clock.Advance(TimeSpan.FromSeconds(1));

        var buffer = new TelemetrySample[SampleRateHz * 4];
        var written = engine.Drain(buffer);

        Assert.True(written > 0);
        Assert.All(
            buffer[..written],
            s =>
            {
                Assert.True(s.RxMegabitsPerSecond > 0);
                // Generous, because the point is that receive follows transmit rather than that
                // the simulation's jitter lands on any particular number.
                Assert.InRange(s.RxMegabitsPerSecond, s.TxMegabitsPerSecond * 0.5, s.TxMegabitsPerSecond * 1.5);
            });
    }

    [Fact]
    public async Task ThroughputStaysBelowLineRate()
    {
        var samples = await CollectAsync(SimulationProfile.Healthy, TimeSpan.FromSeconds(1));
        var lineRate = LinkSpeed.Mbps1000.MegabitsPerSecond();

        Assert.All(samples, s => Assert.True(s.TxMegabitsPerSecond < lineRate));
    }
}
