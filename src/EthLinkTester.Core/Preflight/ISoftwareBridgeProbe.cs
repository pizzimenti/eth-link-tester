namespace EthLinkTester.Core.Preflight;

/// <summary>
/// Looks for anything stacked on an adapter in software that would make the measurement path
/// something other than the cable.
/// </summary>
/// <remarks>
/// A second query rather than a tighter adapter filter, and it has to be: the enumeration filter
/// (<c>HardwareInterface = TRUE AND Virtual = FALSE AND NdisPhysicalMedium = 14</c>) stays true of a
/// physical NIC whether or not a bridge is stacked on it. The bridge is a binding plus a separate
/// virtual miniport, and the NIC underneath is unchanged - so no filter over adapters can see one.
/// </remarks>
public interface ISoftwareBridgeProbe
{
    /// <summary>What is bound to this adapter that should stop or caveat a run.</summary>
    Task<SoftwareBridgeStatus> InspectAsync(
        string adapterId, CancellationToken cancellationToken = default);
}
