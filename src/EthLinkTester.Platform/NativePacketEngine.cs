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
                //
                // The result is deliberately not fatal here. wpcap is delay-loaded, so the engine
                // resolves and answers elt_sample_size perfectly well without Npcap present, and
                // refusing the whole library would turn a missing prerequisite into an unloadable
                // assembly. What must not happen is a *pcap* call reaching Rust with no Npcap
                // behind it - the MSVC delay-load failure path raises a loader exception from
                // inside the engine, outside catch_unwind's contract, which can take the process
                // rather than returning ELT_ERR_OPEN_FAILED. StartAsync gates that directly.
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
        var stale = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (stale != IntPtr.Zero)
        {
            _ = elt_engine_stop(stale);
        }

        // The first pcap call must not be the thing that discovers Npcap is missing. wpcap is
        // delay-loaded, so a missing DLL surfaces as an MSVC loader exception raised from inside
        // the engine on first use - a foreign exception outside `catch_unwind`'s contract, which
        // can terminate the process instead of producing the ELT_ERR_OPEN_FAILED the ABI promises.
        // The Rig page's preflight checks this, but Lab Mode's hardware start does not go through
        // it, so the check belongs here where every hardware run passes.
        if (!NpcapLoader.TryLoad())
        {
            State = EngineState.Idle;
            throw new InvalidOperationException(
                "Npcap is not installed, or is not where this expects it "
                + $"({NpcapLoader.NpcapDirectory}). Install it from https://npcap.com with "
                + "WinPcap-compatible mode turned off.");
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

    /// <summary>
    /// How long receive keeps running after transmit stops, before the engine is torn down.
    /// </summary>
    /// <remarks>
    /// Covers the driver's send queue draining (4 ms of wire time by construction), a sampler
    /// interval for the receive thread to fold its kernel counts in (16.7 ms), and the flight time
    /// of the frames themselves. 500 ms is two orders of magnitude more than that sum, and the
    /// only thing a longer wait can add is confidence that a frame counted as lost really is lost.
    /// </remarks>
    private static readonly TimeSpan Quiesce = TimeSpan.FromMilliseconds(500);

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop sending first, then let the wire and the sampler catch up, then tear down. Counting
        // delivery at the instant transmit ends is short by a fixed amount rather than a random
        // one - frames sitting in the driver's send queue are counted as sent and not yet as
        // received, and the receive thread has kernel counts it has not folded in - so a healthy
        // cable reports loss that is entirely an artefact of when the question was asked. Measured
        // at 0.3% at 1518 bytes on the reference rig, which is the same size as the loss it was
        // being read as.
        //
        // The host keeps draining across this window, so the final samples carry the settled
        // totals. Without it, this engine had the artefact that `enginerun` was fixed for - the
        // number in the app and the number in the tool disagreed about the same cable.
        // Claimed once, here, and used for both calls below. Reading _handle for the
        // transmit-stop and claiming it again for the teardown left a window in which the
        // finalizer could claim and stop the same handle in between - so the transmit-stop ran
        // against a handle another thread had already retired. The comment below says this side
        // must not depend on the native tombstone to stay safe, and that call was depending on it.
        var claimed = Interlocked.Exchange(ref _handle, IntPtr.Zero);

        if (claimed != IntPtr.Zero)
        {
            _ = elt_engine_stop_transmit(claimed);

            try
            {
                await Task.Delay(Quiesce, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled stop still has to stop. Skipping the settle costs accuracy in the
                // final sample; skipping the teardown would leave three threads and two capture
                // devices running.
            }
        }

        // Claimed atomically, because the finalizer can run on another thread while this method is
        // in flight - the object becomes unreachable the moment nothing holds it, which is before
        // DisposeAsync gets to SuppressFinalize. A plain read-then-clear lets both threads see the
        // same non-zero handle and call elt_engine_stop on it twice. That happens to be survivable
        // today only because the native side keeps a tombstone and answers the second call with
        // ELT_ERR_NOT_RUNNING; this side should not be relying on a property of the other side of
        // the ABI to avoid a double free.
        if (claimed != IntPtr.Zero)
        {
            // A non-zero code here means the engine could not shut down cleanly. The run is over
            // either way, but reporting Faulted rather than Idle keeps the host from presenting a
            // half-stopped engine as ready for another run.
            var code = elt_engine_stop(claimed);

            if (code != 0)
            {
                Fault($"The engine did not shut down cleanly: {Describe(code)}");
                return;
            }
        }

        State = EngineState.Idle;
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

        // Before the stop, not after. Between the two there is a window in which this object is
        // unreachable and still holds a live handle, and the finalizer running in that window
        // would race StopAsync for it.
        GC.SuppressFinalize(this);
        await StopAsync().ConfigureAwait(false);
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
        // Same atomic claim as StopAsync, so whichever of the two arrives second finds nothing.
        var claimed = Interlocked.Exchange(ref _handle, IntPtr.Zero);

        if (claimed != IntPtr.Zero)
        {
            // The code is discarded deliberately. There is nobody left to tell: the object is
            // being collected, so no property survives to hold the message and no consumer
            // remains to read one. Freeing the threads and the adapters is the whole point.
            _ = elt_engine_stop(claimed);
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

    // LPUTF8Str, not the ANSI default. The engine reads these with `CStr::to_str`, which is UTF-8
    // and answers ELT_ERR_BAD_UTF8 for anything else, while ANSI marshalling encodes in the
    // system's active code page. The two agree for the ASCII of `\Device\NPF_{GUID}` and diverge
    // the moment a byte above 0x7F appears - a failure that would depend on the machine's locale.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_engine_start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string txDevice,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rxDevice,
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
    private static extern int elt_engine_stop_transmit(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint elt_sample_size();
}
