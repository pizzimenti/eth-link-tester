namespace EthLinkTester.Core.Topology;

/// <summary>
/// What a passive listen heard, and for how long.
/// </summary>
/// <remarks>
/// The window is part of the result rather than a parameter the caller remembers, because silence
/// only means anything relative to it. "Nothing heard" after two seconds and after three minutes
/// are different statements, and only one of them is worth putting in a report.
/// </remarks>
/// <param name="Window">How long both adapters were listening.</param>
/// <param name="Lldp">LLDP frames heard from a source that is not this machine.</param>
/// <param name="Stp">STP, RSTP or MSTP frames, same exclusion.</param>
/// <param name="Cdp">CDP frames, same exclusion.</param>
public readonly record struct PassiveListenResult(TimeSpan Window, int Lldp, int Stp, int Cdp)
{
    /// <summary>Nothing heard at all.</summary>
    public static PassiveListenResult Silent(TimeSpan window) => new(window, 0, 0, 0);

    public int Total => Lldp + Stp + Cdp;

    /// <summary>The protocols that were actually heard, for a report that names them.</summary>
    public IReadOnlyList<string> Protocols =>
        [.. new[] { (Lldp, "LLDP"), (Stp, "STP"), (Cdp, "CDP") }
            .Where(pair => pair.Item1 > 0)
            .Select(pair => pair.Item2)];
}

/// <summary>
/// Reads a passive listen for the protocols a managed device announces itself with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Positive-only, and no observation window changes that.</b> Hearing LLDP, CDP or STP from a
/// source that is not this machine proves a device on the segment, because a cable does not
/// announce itself. Hearing nothing proves nothing at all: an unmanaged switch has no management
/// plane to speak from - the reference NETGEAR GS308 emits nothing whatsoever - so silence is
/// exactly as consistent with a switch as with a bare cable.
/// </para>
/// <para>
/// That asymmetry is why the silent case returns <see cref="TopologyObservation.Nothing"/> and never
/// <see cref="TopologyFinding.Direct"/>. It is the single easiest place in this model to turn
/// absence of evidence into evidence of absence, and doing so would produce a confident direct
/// verdict on every switched path built from an unmanaged switch - which is most of them.
/// </para>
/// <para>
/// Strong rather than Conclusive when something is heard. The frames prove a device is present on
/// the segment; a managed media converter or a monitoring tap that speaks LLDP is a device without
/// being an 802.1 relay, and this signal cannot tell those apart. It is also possible - though the
/// engine's source-MAC exclusion is built to prevent it - for a host's own agent to be heard back
/// on its own handle.
/// </para>
/// </remarks>
public static class BridgeProtocolSignal
{
    /// <summary>
    /// The shortest window in which silence is worth reporting, matching the engine's constant.
    /// </summary>
    /// <remarks>
    /// LLDP announces every 30 s, STP hello is 2 s, and CDP is 60 s with a 180 s hold, so three CDP
    /// intervals plus margin is what makes "heard nothing" a statement about the segment rather than
    /// about patience. The engine's <c>passive::HONEST_SILENCE_SECONDS</c> holds the same value and
    /// asserts the same 180-second floor at compile time; <c>BridgeProtocolSignalTests</c> asserts
    /// the managed half of it.
    /// </remarks>
    public static readonly TimeSpan HonestSilence = TimeSpan.FromSeconds(190);

    /// <summary>Interprets a completed listen.</summary>
    public static TopologyObservation Observe(PassiveListenResult heard)
    {
        if (heard.Total > 0)
        {
            var named = string.Join(" and ", heard.Protocols);

            return new TopologyObservation(
                TopologySignal.BridgeProtocolTraffic,
                TopologyFinding.Bridged,
                SignalStrength.Strong,
                $"Heard {heard.Total} {named} frame{(heard.Total == 1 ? "" : "s")} in "
                + $"{Describe(heard.Window)} from a source that is not this machine. A cable does "
                + "not announce itself, so something on the segment is a device.");
        }

        if (heard.Window < HonestSilence)
        {
            return TopologyObservation.Nothing(
                TopologySignal.BridgeProtocolTraffic,
                $"Nothing heard, but {Describe(heard.Window)} is under the "
                + $"{Describe(HonestSilence)} needed to outlast three CDP intervals. This silence "
                + "is about the length of the listen, not about the segment.");
        }

        return TopologyObservation.Nothing(
            TopologySignal.BridgeProtocolTraffic,
            $"No LLDP, CDP or STP in {Describe(heard.Window)}, which settles nothing. An unmanaged "
            + "switch has no management plane and says nothing at all, so a switched path and a "
            + "bare cable sound identical here. This signal can only ever prove a bridge.");
    }

    private static string Describe(TimeSpan window) =>
        window < TimeSpan.FromMinutes(1)
            ? $"{window.TotalSeconds:0} seconds"
            : $"{window.TotalMinutes:0.#} minutes";
}
