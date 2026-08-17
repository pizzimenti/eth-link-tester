namespace EthLinkTester.Core.Topology;

/// <summary>
/// The two topology measurements that need raw frames, and therefore the engine.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this namespace reasons about adapter state, which the managed side can read
/// for itself. These two cannot be done from here at all: sending to a reserved multicast address
/// and listening promiscuously for LLDP both require injection below the TCP/IP stack, which is the
/// engine's whole purpose.
/// </para>
/// <para>
/// Narrow on purpose. The alternative was for the orchestrator to take a dependency on the packet
/// engine itself, which would drag a run configuration, a telemetry ring and a frame size into a
/// component that wants none of them - and would put a Windows-only, Npcap-dependent type in the
/// path of every topology unit test. Two methods that answer two questions keeps
/// <c>EthLinkTester.Core</c> testable without a driver, a NIC or a cable.
/// </para>
/// </remarks>
public interface ITopologyProbe
{
    /// <summary>
    /// Sends the reserved-multicast sweep from one adapter and reports what reached the other.
    /// </summary>
    /// <remarks>
    /// Returns one <see cref="ProbeResult"/> per address including the control, carrying counts
    /// rather than a crossed flag - the interpretation needs to know how well the path was
    /// delivering, not just whether anything got through. See
    /// <see cref="ReservedMulticastProbe.MinimumControlDelivery"/>.
    /// </remarks>
    Task<IReadOnlyList<ProbeResult>> SweepAsync(
        string transmitAdapterId,
        string receiveAdapterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens on both adapters for the protocols a managed device announces itself with.
    /// </summary>
    /// <param name="adapterIds">Every adapter to listen on, and to exclude as a frame source.</param>
    /// <param name="window">How long to listen.</param>
    /// <remarks>
    /// The implementation must exclude all of this machine's adapters by source MAC. Npcap hands a
    /// capture handle the frames the host itself transmitted on that adapter, so without the
    /// exclusion a host running any LLDP agent reads as a switch on its own segment.
    /// </remarks>
    Task<PassiveListenResult> ListenAsync(
        IReadOnlyList<string> adapterIds,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
