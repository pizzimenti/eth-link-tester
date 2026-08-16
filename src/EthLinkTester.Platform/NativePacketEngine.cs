using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EthLinkTester.Core;
using EthLinkTester.Core.Engine;

namespace EthLinkTester.Platform;

/// <summary>
/// The real engine: a Rust cdylib driving raw Layer 2 traffic through Npcap.
/// </summary>
/// <remarks>
/// <para>
/// Frames must not go over sockets. With two NICs in one host the TCP/IP stack recognises both
/// addresses as local and short-circuits through loopback, so a socket test measures memory
/// bandwidth. That this implementation avoids it is not an assumption - <c>tools\Verify-Wire.ps1</c>
/// asserts 1000 frames leaving one PHY and arriving at the other, counted independently by both
/// NICs' hardware, with nothing coming back on the sender. The <c>wirecheck</c> binary it wraps
/// counts frames in userspace only, which cannot tell a frame that crossed a cable from one a
/// bridge handed back; the counter assertions are what make the claim.
/// </para>
/// <para>
/// Native failures come back as codes rather than exceptions crossing the boundary, and the
/// engine catches its own panics for a specific reason: this process holds the restore journal, so
/// an engine bug must fail the run rather than kill the process and strand a forced adapter.
/// </para>
/// <para>
/// Not thread-safe. One consumer drains, which is what the native ring's single-consumer contract
/// requires anyway; the native side refuses a concurrent drain rather than trusting this.
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
    private bool _disposed;

    static NativePacketEngine() =>
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

                // Npcap first: the engine imports wpcap.dll, and resolving that is what the
                // loader would otherwise fail at. Done here rather than in a static constructor so
                // the work happens when the engine is actually needed, and so merely referencing
                // this type from a test host does not touch the filesystem.
                NpcapLoader.TryLoad();

                var beside = Path.Combine(
                    Path.GetDirectoryName(assembly.Location) ?? string.Empty, Library + ".dll");

                return File.Exists(beside) && NativeLibrary.TryLoad(beside, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });

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

        // Rejected here as well as in the engine. One adapter cannot test a cable, and the failure
        // it produces without this check is the worst kind: the run starts, receives its own frames
        // through the loopback path this whole design exists to avoid, and reports a perfect link.
        if (string.Equals(txDevice, rxDevice, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Transmit and receive must be different adapters, or no frame crosses a cable.",
                nameof(rxDevice));
        }

        _txDevice = txDevice;
        _rxDevice = rxDevice;
        _txMac = txMac;
        _rxMac = rxMac;
    }

    /// <summary>Builds the device name Npcap uses for an adapter GUID.</summary>
    public static string DeviceName(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        // Npcap names its devices for the interface GUID, braces included. Adapters are stored
        // with the braces on Windows, but a caller assembling one by hand often omits them.
        var guid = adapterId.StartsWith('{') ? adapterId : $"{{{adapterId}}}";
        return @"\Device\NPF_" + guid;
    }

    public bool IsSimulated => false;

    public EngineState State { get; private set; } = EngineState.Idle;

    /// <summary>Why the run stopped, or null. Set alongside <see cref="EngineState.Faulted"/>.</summary>
    public string? FaultDescription { get; private set; }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (State == EngineState.Running)
        {
            return ValueTask.CompletedTask;
        }

        // A faulted run is retryable - deliberately, because the faults worth retrying are the
        // ones a user can fix - but the fault left the native engine alive. Only `Running` is
        // rejected above, so without this the assignment at the end of this method would overwrite
        // a still-valid handle and orphan the run behind it: three OS threads and two capture
        // devices with nothing left holding their handle, unreachable by StopAsync and by the
        // finalizer alike. The adapters stay open for the life of the process, and the next start
        // fails with "the adapter is in use by another application" naming no application.
        if (_handle != IntPtr.Zero)
        {
            _ = elt_engine_stop(_handle);
            _handle = IntPtr.Zero;
        }

        // Checked on the way in to the first run rather than at application startup. Doing it at
        // startup would load Npcap on a machine that has none and turn a missing prerequisite -
        // which the preflight check handles gracefully - into a launch failure.
        VerifyLayoutAgreement();

        // Settings carry the wire frame size; the NIC appends the 4-byte FCS, so the buffer handed
        // to pcap is four bytes shorter.
        var bufferBytes = settings.FrameBytes - 4;

        var code = elt_engine_start(
            _txDevice,
            _rxDevice,
            _txMac,
            _rxMac,
            (uint)Math.Max(bufferBytes, 0),
            (ulong)settings.LinkSpeed.BitsPerSecond(),
            out var handle);

        if (code != 0)
        {
            // Idle, not Faulted. A start that never allocated anything leaves nothing to clean up,
            // and the usual causes - Npcap missing, the adapter in use, elevation denied - are all
            // fixed and retried. Marking the object Faulted here bricked it permanently, and then
            // reported the next attempt as "disposed", which is a lie about a live object.
            State = EngineState.Idle;
            throw new InvalidOperationException(Describe(code));
        }

        _handle = handle;
        _droppedSamples = 0;
        FaultDescription = null;
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
                Fault($"The engine did not shut down cleanly: {Describe(code)}");
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
            Fault(Describe(written));
            return 0;
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedSamples, (long)dropped);
        }

        // A dead worker is silent by nature: transmit carries on, the receive count freezes, and
        // the chart renders 100% packet loss on a healthy cable. Asking every drain is the only
        // way the host finds out, and it is one atomic read on the native side.
        var fault = elt_engine_fault(_handle);
        if (fault > 0)
        {
            Fault(DescribeFault(fault));
        }

        return written;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Frees the native handle if the object was abandoned without being disposed.
    /// </summary>
    /// <remarks>
    /// The handle owns three OS threads and two open capture devices. Leaking it leaves them
    /// running for the life of the process, holding adapters open so the next run cannot start -
    /// which reads as "the adapter is in use by another application" and sends the user hunting
    /// for a program that is not there. <see cref="StopAsync"/> is not called from here: it touches
    /// managed state, and a finalizer has no guarantee that state is still alive.
    /// </remarks>
    ~NativePacketEngine()
    {
        if (_handle != IntPtr.Zero)
        {
            // The code is discarded deliberately. There is nobody left to tell: the object is
            // being collected, so no property survives to hold the message and no consumer
            // remains to read one. Freeing the threads and the adapters is the whole point.
            _ = elt_engine_stop(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void Fault(string description)
    {
        State = EngineState.Faulted;
        FaultDescription = description;
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
        -5 => "The engine is stopped, or another call is using it.",
        -6 => "Transmit and receive named the same adapter, so no frame would cross a cable.",
        -7 =>
            $"The frame size is outside {EthernetFrame.MinimumBytes}-9018 bytes, which is what an " +
            "Ethernet link can carry.",
        _ => $"The engine returned an unrecognised code ({code}).",
    };

    private static string DescribeFault(int fault) => fault switch
    {
        1 =>
            "The capture stopped, so nothing is being received. Frames may still be crossing the " +
            "cable; the receive count below is not a measurement of loss.",
        2 => "Transmission stopped, so no further frames were sent.",
        3 => "A worker thread failed. The run was abandoned; adapter settings are unaffected.",
        4 =>
            "Transmit reported more traffic than the link can carry, so frames are being discarded " +
            "before they reach the wire. The usual cause is the link going down mid-run - check " +
            "the cable and both adapters. The throughput and loss figures for this run are not " +
            "measurements.",
        _ => $"The engine reported an unrecognised fault ({fault}).",
    };

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int elt_engine_start(
        string txDevice,
        string rxDevice,
        byte[] txMac,
        byte[] rxMac,
        uint frameLen,
        ulong linkBitsPerSecond,
        out IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int elt_engine_drain(
        IntPtr handle, TelemetrySample* samples, uint capacity, out ulong dropped);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_engine_fault(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_engine_stop(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint elt_sample_size();
}
