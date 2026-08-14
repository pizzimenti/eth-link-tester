using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EthLinkTester.Core.Engine;

namespace EthLinkTester.Core.Tests;

/// <summary>
/// Pins the memory layout the Phase 3 engine will be matched against.
/// </summary>
/// <remarks>
/// The native engine writes these straight into a shared ring that managed code reads through a
/// span over the raw pointer. Nothing checks at run time that the two sides agree, and nothing
/// would fail to compile if they stopped agreeing - the engine would simply read garbage and the
/// app would plot it. These assertions are the only thing that turns "someone reordered two
/// fields" into a failing build rather than a wrong measurement.
/// <para>
/// If a change here is deliberate, the Rust <c>#[repr(C)]</c> definition must change with it.
/// </para>
/// </remarks>
public class TelemetrySampleLayoutTests
{
    [Fact]
    public void IsSixtyFourBytes() =>
        Assert.Equal(64, Unsafe.SizeOf<TelemetrySample>());

    /// <summary>
    /// Field order and offsets, which is what a <c>#[repr(C)]</c> struct is matched against.
    /// </summary>
    [Theory]
    [InlineData(nameof(TelemetrySample.TimestampTicks), 0)]
    [InlineData(nameof(TelemetrySample.TxMegabitsPerSecond), 8)]
    [InlineData(nameof(TelemetrySample.RxMegabitsPerSecond), 16)]
    [InlineData(nameof(TelemetrySample.LatencyP50Microseconds), 24)]
    [InlineData(nameof(TelemetrySample.LatencyP99Microseconds), 32)]
    [InlineData(nameof(TelemetrySample.TxFrames), 40)]
    [InlineData(nameof(TelemetrySample.RxFrames), 48)]
    [InlineData(nameof(TelemetrySample.RxErrors), 56)]
    public void FieldSitsAtItsAgreedOffset(string property, int expectedOffset)
    {
        // Auto-properties are backed by a compiler-named field; match on the property name inside.
        var field = typeof(TelemetrySample)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(f => f.Name.Contains($"<{property}>", StringComparison.Ordinal));

        Assert.Equal(expectedOffset, Marshal.OffsetOf<TelemetrySample>(field.Name).ToInt32());
    }

    /// <summary>
    /// Blittable in the sense the ring needs: no references, so a span can be taken over native
    /// memory without marshalling or pinning games.
    /// </summary>
    [Fact]
    public void ContainsNoReferences() =>
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<TelemetrySample>());

    /// <summary>
    /// A round trip through raw bytes, which is what the ring actually does.
    /// </summary>
    [Fact]
    public void SurvivesReinterpretationFromRawBytes()
    {
        var original = new TelemetrySample
        {
            TimestampTicks = 1234567890123,
            TxMegabitsPerSecond = 941.5,
            RxMegabitsPerSecond = 939.25,
            LatencyP50Microseconds = 120.5,
            LatencyP99Microseconds = 880.75,
            TxFrames = 8_000_000,
            RxFrames = 7_999_997,
            RxErrors = 3,
        };

        Span<TelemetrySample> one = [original];
        var bytes = MemoryMarshal.AsBytes(one);

        Assert.Equal(64, bytes.Length);
        Assert.Equal(original, MemoryMarshal.Read<TelemetrySample>(bytes));
    }
}
