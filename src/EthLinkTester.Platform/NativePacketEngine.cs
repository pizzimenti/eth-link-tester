using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EthLinkTester.Core.Engine;

namespace EthLinkTester.Platform;

/// <summary>
/// The real engine: a Rust cdylib driving raw Layer 2 traffic through Npcap.
/// </summary>
/// <remarks>
/// <para>
/// Frames must not go over sockets. With two NICs in one host the TCP/IP stack recognises both
/// addresses as local and short-circuits through loopback, so a socket test measures memory
/// bandwidth. That this implementation avoids it is not an assumption - the engine's own
/// <c>wirecheck</c> proved 1000 frames leaving one PHY and arriving at the other, counted
/// independently by both NICs' hardware.
/// </para>
/// <para>
/// Native failures come back as codes rather than exceptions crossing the boundary, and the
/// engine catches its own panics for a specific reason: this process holds the restore journal, so
/// an engine bug must fail the run rather than kill the process and strand a forced adapter.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NativePacketEngine : IPacketEngine
{
    private const string Library = "ethlink_engine";

    private readonly string _txDevice;
    private readonly string _rxDevice;
    private readonly byte[] _txMac;
    private readonly byte[] _rxMac;

    private IntPtr _handle;
    private long _droppedSamples;

    static NativePacketEngine()
    {
        NpcapLoader.EnsureSearchPath();

        // Resolves the engine relative to this assembly rather than trusting the search path.
        // "The application directory" is a different place under dotnet run, the packaged app and
        // a test host, and the engine ships beside this assembly in all three.
        NativeLibrary.SetDllImportResolver(
            typeof(NativePacketEngine).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, Library, StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                var beside = Path.Combine(
                    Path.GetDirectoryName(assembly.Location) ?? string.Empty, Library + ".dll");

                return File.Exists(beside) && NativeLibrary.TryLoad(beside, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });
    }

    /// <param name="txDevice">Npcap device name, e.g. <c>\Device\NPF_{GUID}</c>.</param>
    public NativePacketEngine(string txDevice, string rxDevice, byte[] txMac, byte[] rxMac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(txDevice);
        ArgumentException.ThrowIfNullOrWhiteSpace(rxDevice);
        ArgumentNullException.ThrowIfNull(txMac);
        ArgumentNullException.ThrowIfNull(rxMac);

        if (txMac.Length != 6 || rxMac.Length != 6)
        {
            throw new ArgumentException("A MAC address is six bytes.", nameof(txMac));
        }

        _txDevice = txDevice;
        _rxDevice = rxDevice;
        _txMac = txMac;
        _rxMac = rxMac;
    }

    public bool IsSimulated => false;

    public EngineState State { get; private set; } = EngineState.Idle;

    /// <summary>
    /// Telemetry samples the engine overwrote before this host collected them.
    /// </summary>
    /// <remarks>
    /// Non-zero means the history has a hole. It is not frame loss - no traffic was affected - but
    /// a chart that cannot tell a gap from continuity will draw a line straight across it, which
    /// misrepresents how long a stretch of screen covers.
    /// </remarks>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public ValueTask StartAsync(
        EngineRunSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero && State == EngineState.Faulted, this);

        if (State == EngineState.Running)
        {
            return ValueTask.CompletedTask;
        }

        // Settings carry the wire frame size; the NIC appends the 4-byte FCS, so the buffer handed
        // to pcap is four bytes shorter.
        var bufferBytes = Math.Max(settings.FrameBytes - 4, 60);

        var code = elt_engine_start(
            _txDevice, _rxDevice, _txMac, _rxMac, (uint)bufferBytes, out var handle);

        if (code != 0)
        {
            State = EngineState.Faulted;
            throw new InvalidOperationException(Describe(code));
        }

        _handle = handle;
        _droppedSamples = 0;
        State = EngineState.Running;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_handle != IntPtr.Zero)
        {
            // A non-zero code here means the engine could not shut down cleanly. The run is over
            // either way, but reporting Faulted rather than Idle keeps the host from presenting a
            // half-stopped engine as ready for another run.
            var code = elt_engine_stop(_handle);
            _handle = IntPtr.Zero;

            if (code != 0)
            {
                State = EngineState.Faulted;
                return ValueTask.CompletedTask;
            }
        }

        State = EngineState.Idle;
        return ValueTask.CompletedTask;
    }

    public int Drain(Span<TelemetrySample> destination)
    {
        if (_handle == IntPtr.Zero || destination.IsEmpty)
        {
            return 0;
        }

        int written;
        ulong dropped;

        unsafe
        {
            fixed (TelemetrySample* buffer = destination)
            {
                written = elt_engine_drain(_handle, buffer, (uint)destination.Length, out dropped);
            }
        }

        if (written < 0)
        {
            State = EngineState.Faulted;
            return 0;
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedSamples, (long)dropped);
        }

        return written;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Confirms the two sides agree on the telemetry struct before anything is read through it.
    /// </summary>
    /// <remarks>
    /// Nothing checks this at run time otherwise. The layout is pinned on both sides - by
    /// <c>StructLayout</c> and offset tests in managed code, by a size assertion in Rust - but a
    /// mismatched build pairing would slip past both and produce misaligned telemetry that plots
    /// as plausible noise. Failing at startup is far cheaper than debugging that.
    /// </remarks>
    public static void VerifyLayoutAgreement()
    {
        NpcapLoader.EnsureSearchPath();

        var native = elt_sample_size();
        var managed = (uint)Marshal.SizeOf<TelemetrySample>();

        if (native != managed)
        {
            throw new InvalidOperationException(
                $"The engine reports a {native}-byte telemetry sample and this host expects " +
                $"{managed}. The native and managed builds do not match.");
        }
    }

    private static string Describe(int code) => code switch
    {
        -1 => "The engine rejected a null argument.",
        -2 => "A device name was not valid UTF-8.",
        -3 =>
            "Npcap could not open one of the adapters. It is usually a device name that no longer " +
            "matches an adapter, or the process not running elevated.",
        -4 => "The engine faulted internally. The run was abandoned; adapter settings are unaffected.",
        _ => $"The engine returned an unrecognised code ({code}).",
    };

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int elt_engine_start(
        string txDevice,
        string rxDevice,
        byte[] txMac,
        byte[] rxMac,
        uint frameLen,
        out IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int elt_engine_drain(
        IntPtr handle, TelemetrySample* samples, uint capacity, out ulong dropped);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_engine_stop(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint elt_sample_size();
}
