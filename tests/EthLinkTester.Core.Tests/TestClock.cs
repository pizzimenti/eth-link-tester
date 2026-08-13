namespace EthLinkTester.Core.Tests;

/// <summary>
/// A clock the test controls. Small enough not to justify taking a dependency on
/// Microsoft.Extensions.TimeProvider.Testing, and it keeps the engine's sample generation
/// exactly deterministic rather than dependent on how long a test happened to take.
/// </summary>
/// <remarks>
/// Wall clock and monotonic timestamp are tracked separately and can be moved independently.
/// That separation is the point: it is what lets a test reproduce an NTP correction or a VM
/// resume, where system time jumps but elapsed time does not.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    private long _timestamp;

    public DateTimeOffset Now { get; private set; } = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Ticks, so timestamps and TimeSpans share units and stay readable in failures.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => _timestamp;

    /// <summary>Advances both clocks, as real elapsed time does.</summary>
    public void Advance(TimeSpan delta)
    {
        Now += delta;
        _timestamp += delta.Ticks;
    }

    /// <summary>
    /// Moves system time without advancing elapsed time, reproducing an NTP correction, a
    /// manual clock change, or a VM resume. Anything measuring durations from the wall clock
    /// misbehaves here; anything monotonic is unaffected.
    /// </summary>
    public void StepWallClock(TimeSpan delta) => Now += delta;
}
