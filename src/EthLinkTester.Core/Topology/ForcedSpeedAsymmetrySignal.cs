using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// One complete forced-speed cycle: both ports before the force, both after it, and what the write
/// actually did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before-readings are not bookkeeping.</b> Without them the probe cannot tell a link it dragged
/// down to 100 from a link that was sitting at 100 the whole time - and "the free end followed it
/// down" is a claim about a <i>transition</i>. A 10/100 device in the path, a switch port pinned to
/// 100, or a cable that only negotiates 100 all produce a matching pair of readings in which
/// nothing moved, and the probe used to report those as a direct link at a strength that licenses
/// grading.
/// </para>
/// <para>
/// Named properties rather than four positional parameters, because two <c>NetworkAdapterInfo</c>
/// pairs are exactly the kind of argument list that gets transposed silently, and transposing
/// forced with free here inverts the verdict.
/// </para>
/// </remarks>
public sealed record ForcedSpeedProbe
{
    /// <summary>The adapter about to be forced, read before anything was written.</summary>
    public required NetworkAdapterInfo ForcedBefore { get; init; }

    /// <summary>The adapter left alone, read before anything was written.</summary>
    public required NetworkAdapterInfo FreeBefore { get; init; }

    /// <summary>The forced adapter, re-read after the link settled.</summary>
    public required NetworkAdapterInfo ForcedAfter { get; init; }

    /// <summary>The free adapter, re-read after the link settled.</summary>
    public required NetworkAdapterInfo FreeAfter { get; init; }

    /// <summary>What the configurator did when asked to force the speed.</summary>
    public required ConfigurationOutcome Outcome { get; init; }

    /// <summary>The speed the forced adapter was asked to hold.</summary>
    public LinkSpeed Target { get; init; } = LinkSpeed.Mbps100;
}

/// <summary>
/// Reads both ports across a forced-speed cycle, which turns the passive link-speed comparison into
/// a deliberate test.
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
/// the PHYs can resolve master/slave clock roles - IEEE Interpretation 2-07/05 says so
/// unambiguously - so 1000 cannot be pinned; see <see cref="SpeedDuplex.IsTrulyForceable"/>, and
/// note that a driver appearing to offer it as a fixed value is not telling the truth. Forcing one
/// side is sufficient; the asymmetry is the mechanism, not the particular speeds.
/// </para>
/// <para>
/// <b>The before-readings are what close the 100 Mbps switch alias, and it took a rig run to see
/// that.</b> A switch terminates each segment separately, so a speed change at one end never
/// reaches its far port: force A to 100 through a gigabit switch and B stays at a gigabit, which is
/// a mismatch. For B to have been at a gigabit and then follow A down to 100, the change has to
/// have propagated - and propagating is exactly what a relay does not do. The alias only ever
/// existed because the signal could not tell "already at 100" from "moved to 100", which is a
/// missing reading rather than a limit of physics.
/// </para>
/// <para>
/// <b>Duplex is reported and does not decide anything, which is a correction to what this file
/// said first.</b> The reasoning was that auto-negotiation off stops the forced PHY sending fast
/// link pulses, so the partner must fall back on parallel detection - which conveys speed but not
/// duplex and defaults to half (802.3 Clause 28) - making a far end at 100 <i>full</i> proof that
/// something in the path had negotiated. Measured on the reference rig, forcing the Killer E2400 to
/// 100 full brings the Realtek up at 100 <b>full</b>, on a bare cable, within a second and a half.
/// The likeliest explanation is the one this repo already documents elsewhere: a driver asked for a
/// fixed speed may restrict its <i>advertised capability</i> and keep negotiating rather than
/// disabling negotiation at all - see <see cref="SpeedDuplex.IsTrulyForceable"/>, which exists
/// because the other adapter on this rig offers a fixed gigabit it cannot honour.
/// </para>
/// <para>
/// So a duplex-based finding would have reported a bare cable as bridged on the reference hardware.
/// A far end at half duplex is still worth saying - it is the parallel-detection signature, and it
/// means the far PHY really did stop hearing link pulses - but it is colour in the Detail, not a
/// verdict.
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
    /// Interprets a completed forced-speed cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four gates before any conclusion, and each one closes a way of reporting a link state the
    /// probe did not create:
    /// </para>
    /// <list type="number">
    /// <item>The write actually happened. A configurator that finds the property already at the
    /// target skips the write and the journal, which is right for it and lethal here - the adapter
    /// was pinned by something else, most likely a run that died before restoring.</item>
    /// <item>The forced end really is at the target. Drivers accept a <c>*SpeedDuplex</c> write and
    /// keep negotiating; this repo already documents one adapter that does exactly that with its
    /// fictitious forced 1 Gbps. Without this check, a force that did nothing leaves both ends at
    /// 1 Gbps and reads as a direct link "following down to 1 Gbps" - a sentence asserting an
    /// observation nobody made.</item>
    /// <item>The free end was above the target beforehand. Otherwise there was no transition to
    /// witness and a match proves nothing.</item>
    /// <item>Both ends are still up. A link that goes dark says nothing in either direction.</item>
    /// </list>
    /// </remarks>
    public static TopologyObservation Observe(ForcedSpeedProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var (forced, free) = (probe.ForcedAfter, probe.FreeAfter);
        var target = probe.Target;

        if (probe.Outcome == ConfigurationOutcome.AlreadyAtTarget)
        {
            return TopologyObservation.Nothing(
                TopologySignal.ForcedSpeedAsymmetry,
                $"{forced.Name} was already fixed at a forced speed before the test began, so "
                + "nothing was changed and the speeds now showing were not produced by this probe. "
                + "A previous run most likely ended without restoring it. Put the adapter back on "
                + "auto-negotiation and try again.");
        }

        // Dark first: a link that is down has no speed to compare, and both of the checks below
        // would otherwise read its null as a disagreement.
        if (forced.NegotiatedSpeed is not { } forcedSpeed || free.NegotiatedSpeed is not { } freeSpeed)
        {
            var dark = forced.NegotiatedSpeed is null ? forced.Name : free.Name;

            return TopologyObservation.Nothing(
                TopologySignal.ForcedSpeedAsymmetry,
                $"{dark} did not link after {forced.Name} was forced to {target.ShortName()}, so "
                + "the two ends cannot be compared. This says nothing about topology, and it is a "
                + "normal outcome: the far end may not advertise 100BASE-TX, or - the commoner "
                + "cause on a direct pair - automatic MDI/MDI-X crossover is driven by link pulses "
                + "and many PHYs disable it when auto-negotiation is off, so a straight-through "
                + "cable that worked a moment ago can stop linking.");
        }

        // The force is only a test if it landed. A driver that takes the registry write and keeps
        // auto-negotiating leaves both ends where they were, and every comparison below would then
        // be reading a link this probe did not touch.
        if (forcedSpeed != target)
        {
            return TopologyObservation.Nothing(
                TopologySignal.ForcedSpeedAsymmetry,
                $"{forced.Name} was asked to hold {target.ShortName()} and came up at "
                + $"{forcedSpeed.ShortName()}, so the driver accepted the setting without honouring "
                + "it. Nothing was forced, so nothing about the other end's speed is evidence.");
        }

        if (probe.FreeBefore.NegotiatedSpeed is not { } freeBefore || freeBefore <= target)
        {
            var was = probe.FreeBefore.NegotiatedSpeed is { } s ? s.ShortName() : "not linked";

            return TopologyObservation.Nothing(
                TopologySignal.ForcedSpeedAsymmetry,
                $"{free.Name} was {was} before the force, so there was no drop for it to follow. "
                + $"This test compares a before and an after; when the path is already at "
                + $"{target.ShortName()} or below, both readings match whatever the topology is.");
        }

        if (forcedSpeed != freeSpeed)
        {
            return new TopologyObservation(
                TopologySignal.ForcedSpeedAsymmetry,
                TopologyFinding.Bridged,
                SignalStrength.Conclusive,
                $"{forced.Name} was forced from {ForcedBeforeText(probe)} to {target.ShortName()} "
                + $"and {free.Name} stayed at {freeSpeed.ShortName()}. One cable is one link, so an "
                + "end that does not follow the other is not connected to it.");
        }

        // Both ends at the target, having genuinely moved there. A relay would have kept its far
        // port where it was, so the propagation is the evidence.
        return new TopologyObservation(
            TopologySignal.ForcedSpeedAsymmetry,
            TopologyFinding.Direct,
            SignalStrength.Strong,
            $"{forced.Name} was forced from {ForcedBeforeText(probe)} to {target.ShortName()} and "
            + $"{free.Name} followed it down from {freeBefore.ShortName()} to "
            + $"{freeSpeed.ShortName()}{DuplexNote(free.Duplex)}. A switch terminates each segment "
            + "separately, so its far port would have stayed where it was; a change that reached "
            + "both ends crossed one link. A repeater or media converter would also pass it on, "
            + "which is why this is strong rather than conclusive.");
    }

    /// <summary>The forced end's speed before the force, for a Detail that names the transition.</summary>
    private static string ForcedBeforeText(ForcedSpeedProbe probe) =>
        probe.ForcedBefore.NegotiatedSpeed is { } before ? before.ShortName() : "an unlinked state";

    /// <summary>
    /// What the far end's duplex adds, which is context rather than a conclusion.
    /// </summary>
    /// <remarks>
    /// Half duplex is the parallel-detection signature and worth saying: the far PHY stopped
    /// hearing link pulses and fell back on the signalling alone. Full duplex is <i>not</i> evidence
    /// against a direct link, whatever the standards reasoning suggests - the reference rig produces
    /// it on a bare cable, because a driver asked for a fixed speed may restrict advertised
    /// capability and keep negotiating.
    /// </remarks>
    private static string DuplexNote(DuplexMode duplex) => duplex switch
    {
        DuplexMode.Half =>
            " half duplex, which is what a partner falls back to when the other end stops "
            + "negotiating",
        DuplexMode.Full => " full duplex, so the forced end was still negotiating rather than "
            + "signalling blind",
        _ => string.Empty,
    };
}
