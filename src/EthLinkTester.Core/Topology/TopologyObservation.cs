namespace EthLinkTester.Core.Topology;

/// <summary>
/// The five things this app can look at to decide whether a switch sits in the link.
/// </summary>
/// <remarks>
/// Named individually rather than folded into a score, because a report has to say which signals
/// were available and what each one said. "70% confident" with no working is the kind of number
/// this project exists not to produce.
/// </remarks>
public enum TopologySignal
{
    /// <summary>
    /// The two ports negotiated different speeds. Free, and conclusive when it happens: a direct
    /// cable has one link, and one link has one speed.
    /// </summary>
    LinkSpeedMismatch,

    /// <summary>
    /// A frame sent to a reserved link-local multicast address that conforming bridges must not
    /// forward. Arrival means the two PHYs are wired to each other.
    /// </summary>
    ReservedMulticastProbe,

    /// <summary>
    /// LLDP, CDP or STP frames seen without anything of ours having been sent. Something is
    /// announcing itself, and a cable does not announce itself.
    /// </summary>
    BridgeProtocolTraffic,

    /// <summary>
    /// Latency regressed against frame size. A store-and-forward bridge clocks the whole frame in
    /// before sending it on, so it adds a second serialization delay proportional to size.
    /// </summary>
    LatencySlope,

    /// <summary>
    /// One port taken down while the other is watched. Coupled means one link; independent means
    /// something in between is holding the far side up. Disruptive, so opt-in.
    /// </summary>
    LinkStateCoupling,

    /// <summary>
    /// One port forced to 100 Mbps, then both speeds re-read. A deliberate asymmetry, which turns
    /// the free <see cref="LinkSpeedMismatch"/> signal from a passive hope into a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idea is deterministic where the others are statistical: one cable carries one link, so
    /// forcing one end down to 100 drags the other end with it. A switch terminates each segment
    /// separately, so its far port stays where it was and the two ends now disagree - which
    /// <see cref="LinkSpeedMismatch"/> already treats as conclusive.
    /// </para>
    /// <para>
    /// Only one end is forced, and only to 100. IEEE 802.3 requires auto-negotiation at 1000BASE-T
    /// and above so the two PHYs can resolve master/slave clock roles, so 1000 cannot be pinned at
    /// all - and a driver that appears to offer it as a fixed value is not telling the truth, which
    /// this project has already caught one adapter doing. Forcing one side is enough anyway: the
    /// asymmetry is the whole mechanism.
    /// </para>
    /// <para>
    /// Disruptive - it bounces the link and needs the restore journal to put the setting back, so
    /// it is opt-in like <see cref="LinkStateCoupling"/>.
    /// </para>
    /// </remarks>
    ForcedSpeedAsymmetry,
}

/// <summary>What a single signal concluded.</summary>
public enum TopologyFinding
{
    /// <summary>
    /// The signal ran and told us nothing. This is a first-class result, not a failure.
    /// </summary>
    /// <remarks>
    /// Most of these signals are one-directional, and conflating "no evidence" with "evidence of
    /// none" is how a detector produces a confident wrong answer. Seeing no LLDP is the obvious
    /// case: an unmanaged switch may emit nothing at all, so silence is exactly as consistent with
    /// a switch as with a cable.
    /// </remarks>
    Inconclusive,

    /// <summary>This signal indicates the two NICs are wired to each other.</summary>
    Direct,

    /// <summary>This signal indicates something is bridging between them.</summary>
    Bridged,
}

/// <summary>
/// How much weight a finding can carry.
/// </summary>
/// <remarks>
/// Deliberately coarse. A finer scale would invite arithmetic that reads as precision the physics
/// does not support - these are qualitative statements about how a standard behaves, not
/// measurements.
/// </remarks>
public enum SignalStrength
{
    /// <summary>
    /// Settles it on its own. Reserved for signals where the alternative is physically impossible
    /// rather than merely unlikely.
    /// </summary>
    Conclusive,

    /// <summary>Would be surprising to be wrong about, but is not impossible.</summary>
    Strong,

    /// <summary>Points one way. Meaningful only alongside others.</summary>
    Suggestive,
}

/// <summary>
/// One signal's result, with the reason it reached it.
/// </summary>
/// <param name="Signal">Which observation this is.</param>
/// <param name="Finding">What it concluded.</param>
/// <param name="Strength">How much the conclusion is worth.</param>
/// <param name="Detail">
/// What was actually observed, in words a report can print. Never omitted - a finding with no
/// stated basis cannot be argued with, and every verdict this app gives has to be arguable.
/// </param>
public sealed record TopologyObservation(
    TopologySignal Signal,
    TopologyFinding Finding,
    SignalStrength Strength,
    string Detail)
{
    /// <summary>
    /// A signal that ran and settled nothing, which is the common case and must stay cheap to say.
    /// </summary>
    public static TopologyObservation Nothing(TopologySignal signal, string detail) =>
        new(signal, TopologyFinding.Inconclusive, SignalStrength.Suggestive, detail);
}
