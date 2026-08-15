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

        var (stalled, stalledClock) = await RunningEngineAsync(SimulationProfile.Failing);
        stalledClock.Advance(TimeSpan.FromSeconds(seconds));
        var stalledErrors = Collect(stalled)[^1].RxCaptureDrops;

        // The same elapsed time, polled once a second so nothing is ever dropped.
        var (polled, polledClock) = await RunningEngineAsync(SimulationProfile.Failing);
        var polledErrors = 0L;
        for (var i = 0; i < seconds; i++)
        {
            polledClock.Advance(TimeSpan.FromSeconds(1));
            polledErrors = Collect(polled)[^1].RxCaptureDrops;
        }

        Assert.True(
            stalledErrors >= polledErrors * 0.9,
            $"stalled run reported {stalledErrors} errors vs {polledErrors} when polled continuously");
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

    [Fact]
    public async Task HealthyProfileProducesNoErrors()
    {
        var samples = await CollectAsync(SimulationProfile.Healthy, TimeSpan.FromSeconds(1));

        Assert.NotEmpty(samples);
        Assert.Equal(0, samples[^1].RxCaptureDrops);
    }

    [Fact]
    public async Task FailingProfileAccumulatesErrors()
    {
        var samples = await CollectAsync(SimulationProfile.Failing, TimeSpan.FromSeconds(1));

        Assert.True(samples[^1].RxCaptureDrops > 0);
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

    [Fact]
    public async Task UnidirectionalRunReportsNoReceiveTraffic()
    {
        var clock = new TestClock();
        var engine = new SimulatedPacketEngine(SimulationProfile.Healthy, clock);
        await engine.StartAsync(new EngineRunSettings { LinkSpeed = LinkSpeed.Mbps1000, Bidirectional = false });
        clock.Advance(TimeSpan.FromSeconds(1));

        var buffer = new TelemetrySample[SampleRateHz * 4];
        var written = engine.Drain(buffer);

        Assert.True(written > 0);
        Assert.All(buffer[..written], s => Assert.Equal(0, s.RxMegabitsPerSecond));
    }

    [Fact]
    public async Task ThroughputStaysBelowLineRate()
    {
        var samples = await CollectAsync(SimulationProfile.Healthy, TimeSpan.FromSeconds(1));
        var lineRate = LinkSpeed.Mbps1000.MegabitsPerSecond();

        Assert.All(samples, s => Assert.True(s.TxMegabitsPerSecond < lineRate));
    }
}
