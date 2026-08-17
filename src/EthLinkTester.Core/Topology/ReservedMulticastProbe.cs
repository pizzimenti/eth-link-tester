namespace EthLinkTester.Core.Topology;

/// <summary>
/// The destination addresses the multicast sweep uses, and what each one is worth.
/// </summary>
/// <remarks>
/// <para>
/// IEEE 802.1Q gives each reserved address a different guarantee depending on which relay
/// component is in the path, across its Tables 8-1 (C-VLAN/MAC Bridge), 8-2 (S-VLAN) and 8-3
/// (TPMR). The block is not uniform, and the difference decides the whole test.
/// </para>
/// <para>
/// <b>The plan called for <c>01:80:C2:00:00:00</c> and that is the worst address available.</b> It
/// is filtered only by MAC Bridge and C-VLAN components - an S-VLAN component or a Two-Port MAC
/// Relay is *conformant* while forwarding it, and a TPMR is exactly what a media converter, a PoE
/// injector with a switch die, or an inline tap looks like. Worse in practice than in theory:
/// Realtek's RTL8366/8369 and RTL8367N datasheets both specify default-forward for it (strapping
/// pin EN_MLT_FWD, register MCPCR0 = 0x5541), so a default-strapped unmanaged switch built on some
/// of the commonest silicon in cheap 5- and 8-port boxes passes it straight through. A detector
/// resting on that address reports "direct" through such a switch, confidently, which is the
/// failure direction that contaminates every later grade.
/// </para>
/// <para>
/// <b>Declaration order is send order.</b> The engine sends the control first and has a test saying
/// so, because nothing else in the sweep means anything until the path is known to carry frames.
/// This enum used to declare it last, so any future code deriving order from
/// <c>Enum.GetValues</c> would have inverted a tested invariant silently. Both orders are pinned
/// against <c>engine/topology-sweep.txt</c>.
/// </para>
/// </remarks>
public enum ProbeAddress
{
    /// <summary>
    /// A locally-administered group address outside the reserved block, which nothing is entitled
    /// to filter. Sent first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not optional.</b> Without it there is no way to tell "a bridge filtered my probe" from
    /// "the receiving adapter never handed it up" - a promiscuous mode the miniport declined, a
    /// MAC that swallowed the address in hardware, a link still negotiating. Those produce exactly
    /// the same silence a conforming bridge does, and calling that silence a bridge would be
    /// inventing a result out of a broken measurement.
    /// </para>
    /// <para>
    /// Only its <i>arrival</i> is evidence, and its non-arrival is not. 802.1Q 8.8.6 makes "filter
    /// unregistered groups" one of three conformant per-port modes, so a bridge may drop this while
    /// behaving correctly. That lands safely - the sweep then says nothing rather than something
    /// wrong - but it means a lost control frame cannot be read as "no path".
    /// </para>
    /// </remarks>
    Control,

    /// <summary>
    /// <c>01:80:C2:00:00:02</c>, Slow Protocols. The discriminating probe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtered by every 802.1Q relay component type, and specified as always-filtered and
    /// non-overridable in both Realtek generations read. If anything relays this, it is not an
    /// 802.1 bridge at all. It is also the only member of the block that behaves the same across
    /// every vendor whose datasheet was read - Broadcom drops all three probes, Realtek gigabit
    /// silicon drops only this one - which is why it and nothing else decides.
    /// </para>
    /// <para>
    /// One deliberate nonconformance, recorded where the address is defined rather than buried:
    /// 802.3 Annex 57A.3 allocates this address "exclusively for use by Slow Protocols PDUs", so
    /// sending anything else to it is out of spec. It is harmless - 57A.4 makes the Slow Protocols
    /// demultiplexer EtherType-gated, so a conforming receiver hands a <c>0x88B5</c> frame to
    /// nothing at all - but it is a rule this tool knowingly breaks and should own.
    /// </para>
    /// </remarks>
    SlowProtocols,

    /// <summary>
    /// <c>01:80:C2:00:00:04</c>, MAC-specific Control Protocols. The safe corroborator.
    /// </summary>
    /// <remarks>
    /// Filtered by every component type, and the only address in the block with no protocol
    /// assigned to it in practice - nothing anywhere is listening for it, which makes it the
    /// safest frame in the sweep to put on someone's live network.
    /// </remarks>
    MacControlProtocols,

    /// <summary>
    /// <c>01:80:C2:00:00:0E</c>, Individual LAN Scope / Nearest Bridge. The middle signal.
    /// </summary>
    /// <remarks>
    /// Carries the strongest normative language in the IEEE listing - "no IEEE 802.1 relay device
    /// will be defined that will forward frames that carry this destination address" - and yet
    /// leaks through cheap switches often enough to be documented in the field. That gap between
    /// what the standard intends and what silicon does is precisely why it is worth sending.
    /// </remarks>
    NearestBridge,
}

/// <summary>
/// The wire facts about each sweep address, matching <c>engine/src/topology.rs</c>.
/// </summary>
/// <remarks>
/// Held here so the addresses a report prints come from one place rather than being retyped into
/// each Detail string, which is how a report ends up naming an address the sweep never sent.
/// </remarks>
public static class ProbeAddressExtensions
{
    /// <summary>The destination MAC, in the form a report prints.</summary>
    public static string Mac(this ProbeAddress address) => address switch
    {
        ProbeAddress.Control => "03:00:5E:00:00:01",
        ProbeAddress.SlowProtocols => "01:80:C2:00:00:02",
        ProbeAddress.MacControlProtocols => "01:80:C2:00:00:04",
        ProbeAddress.NearestBridge => "01:80:C2:00:00:0E",
        _ => throw new ArgumentOutOfRangeException(nameof(address), address, null),
    };

    /// <summary>True for the one address whose filtering decides the verdict.</summary>
    public static bool Decides(this ProbeAddress address) => address == ProbeAddress.SlowProtocols;
}

/// <summary>
/// How many frames were sent to one address and how many arrived at the far adapter.
/// </summary>
/// <remarks>
/// <b>Counts, not a bool.</b> This was <c>bool Crossed</c>, and the two questions the sweep asks are
/// not equally served by one: a single arrival proves forwarding outright, because filtering is not
/// a lossy process, while <i>no</i> arrivals only means something if the path was delivering frames
/// at the time. Collapsing both to a bool at the seam threw away the counts the second question
/// needs, and left the threshold - what fraction of frames counts as "crossed" - defined on neither
/// side of it.
/// </remarks>
/// <param name="Address">Which address was sent to.</param>
/// <param name="Sent">How many frames were sent.</param>
/// <param name="Arrived">How many were captured at the far adapter.</param>
public readonly record struct ProbeResult(ProbeAddress Address, int Sent, int Arrived)
{
    /// <summary>True when at least one frame reached the far adapter.</summary>
    /// <remarks>
    /// One is enough, and the asymmetry is real rather than sloppy: a relay component either
    /// applies its filtering rules to an address or it does not, so a single arrival settles that
    /// it does not. The converse needs <see cref="ReservedMulticastProbe"/>'s delivery gate.
    /// </remarks>
    public bool Crossed => Arrived > 0;

    /// <summary>The fraction that arrived, or zero when nothing was sent.</summary>
    public double DeliveryRate => Sent <= 0 ? 0 : (double)Arrived / Sent;
}

/// <summary>
/// Reads a sweep of reserved-multicast probes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a corroborating signal, not a primary one.</b> The plan proposed it as the primary
/// detector on the strength of the 802.1D filtering requirement, and the silicon does not honour
/// that requirement reliably enough to carry a verdict. What it is good at is the opposite job:
/// when something *is* filtering, that is hard evidence, and the pattern of which addresses crossed
/// is a fingerprint of the intervening device.
/// </para>
/// <para>
/// <b>That asymmetry is in the strengths, and it has to be.</b> Filtered returns
/// <see cref="SignalStrength.Strong"/>; crossed returns <see cref="SignalStrength.Suggestive"/>.
/// The two branches used to return Strong alike, three lines apart, under a comment saying crossing
/// was the weaker of the two - and since <see cref="TopologyVerdict.From"/> concludes Direct from
/// any single Strong observation and <see cref="TopologyVerdict.GradingIsAttributable"/> unlocks at
/// the confidence that produces, a sweep run on its own could license grading a cable through a
/// media converter. Every prose statement in this file said the signal could not do that while the
/// code let it. Suggestive is what "corroborating" means when written as a value: it lifts another
/// signal's confidence and reaches no conclusion alone.
/// </para>
/// <para>
/// Every frame goes out with EtherType <c>0x88B5</c> - IEEE 802a Local Experimental Ethertype 1,
/// per RFC 5342 - and never the protocol's own EtherType. A frame to the Slow Protocols address
/// carrying <c>0x8809</c> is an LACP frame as far as anything listening is concerned, and this tool
/// has no business injecting those into a network it did not build. The engine already uses
/// <c>0x88B5</c>, so the sweep inherits it.
/// </para>
/// </remarks>
public static class ReservedMulticastProbe
{
    /// <summary>
    /// How much of the control has to arrive before the discriminator's silence means anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The converse gate, and without it a bad cable reads as a switch.</b> One control frame in
    /// twenty used to be enough to call the path "measuring". On a link losing 95% of frames - a
    /// marginal cable, which is this tool's own subject - the control scrapes through on one frame
    /// while all twenty discriminator frames are lost by chance with probability 0.95²⁰, about 36%.
    /// The sweep then reports that something in the path is applying 802.1 filtering rules, and a
    /// user goes looking for a switch nobody installed.
    /// </para>
    /// <para>
    /// Three quarters, for two reasons that happen to agree. Statistically, at that delivery rate
    /// losing twenty consecutive frames by chance is about one in 10¹²; the reference rig's
    /// direct-cable baseline is 20 of 20 on every address, so this is not a tight bar in practice.
    /// Practically, a path dropping more than a quarter of minimum-size frames has a problem the
    /// topology sweep is not the right instrument to describe, and saying so is more use than
    /// naming a switch.
    /// </para>
    /// </remarks>
    public const double MinimumControlDelivery = 0.75;

    /// <summary>
    /// Interprets a completed sweep.
    /// </summary>
    /// <param name="results">One entry per address sent, including the control.</param>
    public static TopologyObservation Observe(IReadOnlyList<ProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var control = results.FirstOrDefault(r => r.Address == ProbeAddress.Control);

        // No control result at all, or a control that did not arrive: the path itself is not
        // carrying frames, so nothing else in the sweep means anything. Silence from a bridge and
        // silence from a broken capture are the same silence.
        if (results.All(r => r.Address != ProbeAddress.Control) || !control.Crossed)
        {
            return TopologyObservation.Nothing(
                TopologySignal.ReservedMulticastProbe,
                "The control frame, which nothing is entitled to filter, did not arrive. The sweep "
                + "cannot distinguish a bridge from a path that is not delivering frames at all, "
                + "so it says nothing either way. A bridge configured to filter unregistered "
                + "multicast groups would also look like this, which is conformant behaviour.");
        }

        var discriminator = results.FirstOrDefault(r => r.Address == ProbeAddress.SlowProtocols);
        if (results.All(r => r.Address != ProbeAddress.SlowProtocols))
        {
            return TopologyObservation.Nothing(
                TopologySignal.ReservedMulticastProbe,
                "The Slow Protocols probe was not sent, so the sweep has no discriminating result.");
        }

        var fingerprint = Fingerprint(results);
        var address = ProbeAddress.SlowProtocols.Mac();

        if (discriminator.Crossed)
        {
            // Crossing is weaker than being filtered, and deliberately so. Every 802.1 relay
            // component type filters this address, so a bridge that passes it is out of spec - but
            // a media converter, a PHY-level repeater and a passive tap are not relay components at
            // all, and pass everything. "Nothing filtered it" is not "nothing is there", so this
            // corroborates a direct link and cannot conclude one.
            return new TopologyObservation(
                TopologySignal.ReservedMulticastProbe,
                TopologyFinding.Direct,
                SignalStrength.Suggestive,
                $"{address} crossed ({Delivery(discriminator)}), which no conforming 802.1 relay "
                + "component would allow. A media converter, a PHY-level repeater or a passive tap "
                + $"would also pass it, so this supports a direct link without establishing one. "
                + fingerprint);
        }

        // Nothing arrived on the discriminator. That is only a statement about filtering if the
        // path was carrying frames well enough for twenty consecutive losses to be implausible.
        if (control.DeliveryRate < MinimumControlDelivery)
        {
            return TopologyObservation.Nothing(
                TopologySignal.ReservedMulticastProbe,
                $"{address} did not arrive, but neither did much else: the control frame, which "
                + $"nothing may filter, managed only {Delivery(control)}. On a path losing frames "
                + "at that rate the whole probe can go missing by chance, so this cannot be read as "
                + "filtering. The delivery rate is the finding here, and it is a cable problem "
                + "rather than a topology one.");
        }

        return new TopologyObservation(
            TopologySignal.ReservedMulticastProbe,
            TopologyFinding.Bridged,
            SignalStrength.Strong,
            $"The control frame crossed ({Delivery(control)}) and {address} did not "
            + $"({Delivery(discriminator)}), so something in the path is filtering reserved "
            + $"addresses. {fingerprint}");
    }

    /// <summary>"18 of 20", which is what a report needs to argue with the verdict.</summary>
    private static string Delivery(ProbeResult result) => $"{result.Arrived} of {result.Sent}";

    /// <summary>
    /// Describes which addresses crossed, because the pattern identifies the device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth reporting even when the verdict is settled elsewhere. All three reserved probes
    /// crossing looks like a cable; <c>-04</c> and <c>-0E</c> crossing while <c>-02</c> is absorbed
    /// is the Realtek gigabit default strap; nothing crossing is Broadcom-class silicon behaving
    /// conformantly. That is a more useful line in a report than a verdict on its own, and it costs
    /// nothing extra to collect.
    /// </para>
    /// <para>
    /// Three, not sixteen. This remark used to describe a sweep of the whole
    /// <c>01:80:C2:00:00:00</c>–<c>0F</c> block, which is what the plan proposed and not what was
    /// built: <c>-00</c> is conformantly forwarded by S-VLAN and TPMR components, <c>-01</c> is
    /// PAUSE and is consumed by essentially every receiving MAC, and both are excluded by
    /// build-failing tests in <c>engine/src/topology.rs</c>. A reader sizing this at sixteen-way
    /// identification would over-read what the pattern can say.
    /// </para>
    /// </remarks>
    private static string Fingerprint(IReadOnlyList<ProbeResult> results)
    {
        var crossed = results
            .Where(r => r.Address != ProbeAddress.Control && r.Crossed)
            .Select(r => r.Address.ToString())
            .ToList();

        return crossed.Count == 0
            ? "No reserved address crossed."
            : $"Reserved addresses that crossed: {string.Join(", ", crossed)}.";
    }
}
