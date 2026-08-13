namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Read-only access to the machine's network adapters.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has no mutating members. Changing an adapter property is only safe once the
/// write-ahead restore journal exists, so that capability lives on a separate interface added
/// later in Phase 2. Splitting them means the ordering rule is enforced by the type system
/// rather than by remembering it.
/// </para>
/// <para>
/// Implementations return physical adapters only. The reference machine carries Tailscale's
/// wintun device, several WAN miniports, a kernel debug adapter, and two Wi-Fi Direct virtual
/// adapters - offering any of those as a test endpoint would waste the user's time at best.
/// </para>
/// </remarks>
public interface IAdapterProvider
{
    /// <summary>Physical adapters, virtual and pseudo-adapters excluded.</summary>
    Task<IReadOnlyList<NetworkAdapterInfo>> GetPhysicalAdaptersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Probes what the driver actually permits, rather than assuming.</summary>
    Task<AdapterCapabilities> ProbeCapabilitiesAsync(
        string adapterId, CancellationToken cancellationToken = default);

    /// <summary>Reads the NIC's own counters. The source of truth for loss and volume.</summary>
    Task<AdapterCounters> ReadCountersAsync(
        string adapterId, CancellationToken cancellationToken = default);

    /// <summary>Frequency of the timestamps in <see cref="AdapterCounters.TimestampTicks"/>.</summary>
    long TimestampFrequency { get; }
}
