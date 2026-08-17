using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EthLinkTester.Core.Topology;

namespace EthLinkTester.Platform;

/// <summary>
/// The two topology measurements that need raw frames, run by the Rust engine.
/// </summary>
/// <remarks>
/// <para>
/// Both entry points are synchronous and blocking on the native side, which is the honest shape for
/// what they are: a fixed short burst and a fixed-length listen, neither of which produces telemetry
/// while it runs. A handle, a polling loop and a state machine would be machinery around a function
/// call. They are pushed onto the thread pool here so a caller can await them and the UI thread is
/// never the one waiting three minutes for a CDP interval.
/// </para>
/// <para>
/// The sweep's arrival counts come back positionally, in the order the engine sends them - control
/// first. That agreement is what <c>engine/topology-sweep.txt</c> pins on both sides, and
/// <see cref="ProbeAddress"/>'s declaration order is the managed half of it, so the mapping here is
/// an index and not a lookup table that could drift from either.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NativeTopologyProbe : ITopologyProbe
{
    private const string Library = NativeEngineLibrary.Name;

    static NativeTopologyProbe() => NativeEngineLibrary.EnsureResolverRegistered();

    /// <summary>Frames sent to each address, matching the engine's own default.</summary>
    /// <remarks>
    /// Twenty, because one lost frame must not read as a filtered one - and because the delivery
    /// gate in <see cref="ReservedMulticastProbe"/> reasons about a denominator. A caller that
    /// halves this halves the confidence in every "did not arrive" the sweep produces.
    /// </remarks>
    public const uint Repeats = 20;

    private readonly Func<string, string> _resolveDevice;
    private readonly Func<string, byte[]> _resolveMac;

    /// <param name="resolveDevice">Turns an adapter id into an Npcap device name.</param>
    /// <param name="resolveMac">Turns an adapter id into its six-byte MAC.</param>
    /// <remarks>
    /// Injected rather than looked up here, because this class has no business enumerating
    /// adapters - the caller already holds them, and resolving them twice is how two components
    /// come to disagree about which adapter is which.
    /// </remarks>
    public NativeTopologyProbe(Func<string, string> resolveDevice, Func<string, byte[]> resolveMac)
    {
        ArgumentNullException.ThrowIfNull(resolveDevice);
        ArgumentNullException.ThrowIfNull(resolveMac);

        _resolveDevice = resolveDevice;
        _resolveMac = resolveMac;
    }

    public Task<IReadOnlyList<ProbeResult>> SweepAsync(
        string transmitAdapterId,
        string receiveAdapterId,
        CancellationToken cancellationToken = default)
    {
        var addresses = Enum.GetValues<ProbeAddress>();
        var expected = elt_sweep_addresses();

        if (expected != addresses.Length)
        {
            // A count mismatch means the engine and this build disagree about the sweep itself, and
            // every result would then be attributed to the wrong address. Loud, because a silent
            // off-by-one here reads as a fingerprint rather than as a broken build.
            throw new InvalidOperationException(
                $"The engine sweeps {expected} addresses and this build knows {addresses.Length}. " +
                "The native and managed builds do not match.");
        }

        var device = (Transmit: _resolveDevice(transmitAdapterId),
                      Receive: _resolveDevice(receiveAdapterId));
        var mac = _resolveMac(transmitAdapterId);

        return Task.Run<IReadOnlyList<ProbeResult>>(
            () =>
            {
                var arrived = new uint[addresses.Length];
                var code = elt_topology_sweep(
                    device.Transmit, device.Receive, mac, Repeats, arrived, (uint)arrived.Length);

                if (code != 0)
                {
                    throw new InvalidOperationException(Describe(code));
                }

                return [.. addresses.Select(
                    (address, index) => new ProbeResult(address, (int)Repeats, (int)arrived[index]))];
            },
            cancellationToken);
    }

    public Task<PassiveListenResult> ListenAsync(
        IReadOnlyList<string> adapterIds,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapterIds);

        // Exactly two, because that is what a link has ends. A wider signature would suggest this
        // scales to a segment with three listeners, which the engine's entry point does not.
        if (adapterIds.Count != 2)
        {
            throw new ArgumentException(
                "A passive listen covers exactly the two adapters under test.", nameof(adapterIds));
        }

        var devices = adapterIds.Select(_resolveDevice).ToArray();
        var macs = adapterIds.Select(_resolveMac).ToArray();
        var seconds = (uint)Math.Max(0, Math.Round(window.TotalSeconds));

        return Task.Run(
            () =>
            {
                var counts = new uint[3];
                var code = elt_passive_listen(
                    devices[0], devices[1], macs[0], macs[1], seconds, counts, (uint)counts.Length);

                if (code != 0)
                {
                    throw new InvalidOperationException(Describe(code));
                }

                return new PassiveListenResult(
                    window, (int)counts[0], (int)counts[1], (int)counts[2]);
            },
            cancellationToken);
    }

    private static string Describe(int code) => NativeEngineLibrary.Describe(code);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint elt_sweep_addresses();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_topology_sweep(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string txDevice,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string rxDevice,
        byte[] txMac,
        uint repeats,
        [Out] uint[] arrived,
        uint arrivedLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int elt_passive_listen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceA,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceB,
        byte[] macA,
        byte[] macB,
        uint seconds,
        [Out] uint[] counts,
        uint countsLength);
}
