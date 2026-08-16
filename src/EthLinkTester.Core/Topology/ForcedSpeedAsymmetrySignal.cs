using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// Reads both ports after one of them has been forced to 100 Mbps, which turns the passive
/// link-speed comparison into a deliberate test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the strongest signal available.</b> The others are statistical or depend on how a
/// particular switch behaves. This one rests on the line codes: 100BASE-TX is MLT-3 over two pairs
/// and 1000BASE-T is PAM-5 over four, so two PHYs cannot carry a link between them at different
/// speeds. One cable is one link, and forcing one end down to 100 drags the other end with it. A
/// switch terminates each segment independently, so its far port stays where it was and the two
/// ends now disagree - which <see cref="LinkSpeedSignal"/> already treats as conclusive.
/// </para>
/// <para>
/// It is also immune to the thing that undermines the latency slope: a cut-through switch, a media
/// converter and a store-and-forward switch all terminate their segments separately, so all three
/// show the asymmetry. The slope test cannot see a cut-through switch at all.
/// </para>
/// <para>
/// <b>Only one end is forced, and only to 100.</b> 802.3 requires auto-negotiation at 1000BASE-T so
/// the PHYs can resolve master/slave clock roles, so 1000 cannot be pinned - see
/// <see cref="SpeedDuplex.IsTrulyForceable"/>, and note that a driver appearing to offer it as a
/// fixed value is not telling the truth. Forcing one side is sufficient; the asymmetry is the
/// mechanism, not the particular speeds.
/// </para>
/// <para>
/// <b>This mutates adapter state and must run under the restore journal.</b> The caller is
/// responsible for recording the original <c>*SpeedDuplex</c> before the change and putting it back
/// after - forcing a speed on the adapter carrying the default route drops that connection, and a
/// crash mid-test leaves it stranded. This type only interprets the result.
/// </para>
/// </remarks>
public static class ForcedSpeedAsymmetrySignal
{
    /// <summary>
    /// Interprets the two ports' speeds after <paramref name="forced"/> was pinned to 100 Mbps.
    /// </summary>
    /// <param name="forced">The adapter that was forced, re-read after the link settled.</param>
    /// <param name="free">The adapter left on auto-negotiation, re-read after the link settled.</param>
    public static TopologyObservation Observe(NetworkAdapterInfo forced, NetworkAdapterInfo free)
    {
        ArgumentNullException.ThrowIfNull(forced);
        ArgumentNullException.ThrowIfNull(free);

        // No link at all after forcing is a real outcome and not a bridge. A direct pair whose far
        // end cannot do 100, or whose autonegotiation will not fall back cleanly against a forced
        // partner, simply goes dark - which says nothing about topology and must not be read as
        // one, in either direction.
        if (forced.NegotiatedSpeed is not { } forcedSpeed || free.NegotiatedSpeed is not { } freeSpeed)
        {
            var dark = forced.NegotiatedSpeed is null ? forced.Name : free.Name;

            return TopologyObservation.Nothing(
                TopologySignal.ForcedSpeedAsymmetry,
                $"{dark} did not link after {forced.Name} was forced to 100 Mbps, so the two ends "
                + "cannot be compared. This says nothing about topology.");
        }

        if (forcedSpeed != freeSpeed)
        {
            return new TopologyObservation(
                TopologySignal.ForcedSpeedAsymmetry,
                TopologyFinding.Bridged,
                SignalStrength.Conclusive,
                $"{forced.Name} was forced to 100 Mbps and {free.Name} stayed at "
                + $"{freeSpeed.ShortName()}. One cable is one link, so an end that does not follow "
                + "the other is not connected to it.");
        }

        // Strong, not Conclusive. The far end following down to 100 is what a cable does - but it
        // is also what a 100 Mbps switch does, and what a switch does when the free adapter cannot
        // exceed 100 either. Those are unusual on a gigabit bench and not impossible anywhere.
        return new TopologyObservation(
            TopologySignal.ForcedSpeedAsymmetry,
            TopologyFinding.Direct,
            SignalStrength.Strong,
            $"{forced.Name} was forced to 100 Mbps and {free.Name} followed it down to "
            + $"{freeSpeed.ShortName()}. A link that changes speed at both ends together is one "
            + "link. A 100 Mbps switch would look the same, which is why this is not conclusive.");
    }
}
