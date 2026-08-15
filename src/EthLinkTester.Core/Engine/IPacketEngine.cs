namespace EthLinkTester.Core.Engine;

public enum EngineState
{
    Idle,
    Running,
    Faulted,
}

/// <summary>Parameters for a single traffic run.</summary>
public sealed record EngineRunSettings
{
    /// <summary>The negotiated link rate, which sets the theoretical ceiling for the run.</summary>
    public required LinkSpeed LinkSpeed { get; init; }

    /// <summary>
    /// Ethernet frame size in bytes. RFC 2544 sweeps 64 through 1518; 64-byte frames are the
    /// stress case because they maximise frames per second rather than bits per second.
    /// </summary>
    public int FrameBytes { get; init; } = 1518;

    public bool Bidirectional { get; init; } = true;
}

/// <summary>
/// Generates traffic across the link under test and reports telemetry.
/// </summary>
/// <remarks>
/// <para>
/// The contract is shaped by the native implementation rather than by what is convenient for a
/// mock. <see cref="Drain"/> takes a caller-owned span and returns a count, mirroring how
/// managed code will read the engine's unmanaged ring buffer: the consumer polls, copies out
/// whatever has accumulated, and allocates nothing. Writing the interface around an event or an
/// <c>IAsyncEnumerable</c> would have read more naturally in C# and then needed reshaping the
/// moment the real engine landed.
/// </para>
/// <para>
/// Implementations must never move traffic over sockets. With two NICs in one host, the Windows
/// TCP/IP stack recognises both addresses as local and short-circuits through the loopback path,
/// so the frames never reach copper and the measurement is meaningless.
/// </para>
/// </remarks>
public interface IPacketEngine : IAsyncDisposable
{
    /// <summary>
    /// True when telemetry is synthesised rather than measured. The UI must surface this
    /// prominently: a plausible-looking chart that never touched a cable is worse than no chart.
    /// </summary>
    bool IsSimulated { get; }

    EngineState State { get; }

    ValueTask StartAsync(EngineRunSettings settings, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples the engine produced that this consumer never collected, cumulative for the run.
    /// </summary>
    /// <remarks>
    /// Not frame loss - no traffic is affected, and a run with a large count here is still a valid
    /// measurement. It matters because the history has a hole: a chart that cannot distinguish a
    /// gap from continuity draws a line straight across it, so a stretch of screen silently
    /// represents more elapsed time than it appears to. The count is what lets the consumer mark
    /// the gap instead.
    /// </remarks>
    long DroppedSamples { get; }

    /// <summary>
    /// Copies pending samples into <paramref name="destination"/> and returns how many were
    /// written. Returns 0 when the engine is idle or nothing new has accumulated.
    /// </summary>
    int Drain(Span<TelemetrySample> destination);
}
