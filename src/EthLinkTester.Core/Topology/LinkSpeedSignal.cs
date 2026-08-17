using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// Compares what the two ports negotiated. The cheapest signal there is, and the only one that
/// costs nothing to run because the numbers are already on screen.
/// </summary>
/// <remarks>
/// <para>
/// One cable carries one link, and one link resolves to one speed at both ends - auto-negotiation
/// is a conversation between exactly two PHYs. So two ports reporting different speeds cannot be
/// looking at each other, and something is terminating the link at each end separately. That makes
/// a mismatch <see cref="SignalStrength.Conclusive"/>: the alternative is not unlikely, it is
/// impossible.
/// </para>
/// <para>
/// Agreement proves nothing at all, and that is the trap. Two ports at 1 Gbps is exactly what a
/// direct cable looks like and exactly what a gigabit switch looks like, which is why this returns
/// <see cref="TopologyFinding.Inconclusive"/> rather than <see cref="TopologyFinding.Direct"/> for
/// the matching case. Reading agreement as evidence of a direct link would be the single easiest
/// way to produce the confident wrong answer this whole model is shaped to avoid.
/// </para>
/// <para>
/// <b>The one soft spot in "impossible": the two speeds are not read at the same instant.</b> The
/// physics is exact but the evidence for it is two CIM queries some milliseconds apart, and a link
/// that retrains between them - a downshift under thermal stress, precisely the fault this tool
/// exists to catch - fabricates a mismatch on a bare cable. It fails in the cheap direction, so it
/// is not urgent, but "the alternative is physically impossible" is a claim about the link and not
/// about the reading. The orchestrator should re-read and confirm before promoting this to
/// Conclusive; a caller reading both ports twice costs one more query and turns a transient into
/// something this signal can see rather than something it reports.
/// </para>
/// </remarks>
public static class LinkSpeedSignal
{
    /// <summary>
    /// Observes the two adapters' negotiated speeds.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either adapter is null.</exception>
    public static TopologyObservation Observe(NetworkAdapterInfo transmit, NetworkAdapterInfo receive)
    {
        ArgumentNullException.ThrowIfNull(transmit);
        ArgumentNullException.ThrowIfNull(receive);

        // A speed neither adapter will report is not a match and not a mismatch. An unplugged or
        // still-negotiating port reads as null here, and treating that as agreement would let a
        // dead link look like a healthy direct one.
        if (transmit.NegotiatedSpeed is not { } transmitSpeed
            || receive.NegotiatedSpeed is not { } receiveSpeed)
        {
            var unknown = transmit.NegotiatedSpeed is null ? transmit.Name : receive.Name;

            return TopologyObservation.Nothing(
                TopologySignal.LinkSpeedMismatch,
                $"{unknown} reports no negotiated speed, so the two ends cannot be compared.");
        }

        if (transmitSpeed != receiveSpeed)
        {
            return new TopologyObservation(
                TopologySignal.LinkSpeedMismatch,
                TopologyFinding.Bridged,
                SignalStrength.Conclusive,
                $"{transmit.Name} negotiated {transmitSpeed.ShortName()} and {receive.Name} "
                + $"negotiated {receiveSpeed.ShortName()}. One cable resolves to one speed at both "
                + "ends, so the two ports are not looking at each other.");
        }

        return TopologyObservation.Nothing(
            TopologySignal.LinkSpeedMismatch,
            $"Both ports negotiated {transmitSpeed.ShortName()}, which a direct cable and a switch "
            + "of the same speed produce alike.");
    }
}
