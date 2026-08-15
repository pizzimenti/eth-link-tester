using System.Runtime.InteropServices;

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
/// The layout is <b>declared</b> rather than relied upon. Reference-free structs happen to get
/// sequential layout by default, which is what makes this work today - but that is a compiler
/// detail, not a promise, and adding one <c>bool</c> or reordering two fields would silently
/// change the packing that a Rust <c>#[repr(C)]</c> struct is matched against. Nothing would
/// fail to compile; the engine would just read garbage. <c>TelemetrySampleLayoutTests</c> pins
/// the size and every offset so a change has to be deliberate.
/// </para>
/// <para>
/// The <c>required</c> members are a compile-time convenience for managed construction and
/// nothing more. A sample materialised from native memory bypasses them entirely, so they are
/// not a runtime guarantee that any field was populated.
/// </para>
/// <para>
/// Latency is carried as percentiles rather than a mean on purpose. A marginal cable shows up
/// as tail latency; averaging hides exactly the signal worth having.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
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
    /// Cumulative frames the kernel matched but this process's capture buffer could not take.
    /// </summary>
    /// <remarks>
    /// <b>Not link errors and not cable loss.</b> It was labelled "receive errors" and shown on
    /// the Lab page under that name, which invites precisely the wrong conclusion: the number
    /// rises when the host cannot keep up, so a fast machine with a broken cable reads zero and a
    /// busy machine with a perfect one reads thousands. It is a caveat on the measurement's
    /// completeness, not a measurement of the link.
    /// <para>
    /// Real frame loss is <c>TxFrames</c> minus <c>RxFrames</c> - two counters read from two
    /// drivers - cross-checked against the adapters' own hardware counters. Loss caused by a cable
    /// appears in no receive counter at all, which is why it has to be derived rather than read.
    /// </para>
    /// </remarks>
    public required long RxCaptureDrops { get; init; }

    public DateTimeOffset Timestamp => new(TimestampTicks, TimeSpan.Zero);
}
