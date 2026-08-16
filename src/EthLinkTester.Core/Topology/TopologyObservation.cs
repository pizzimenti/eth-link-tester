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
    /// <remarks>
    /// <para>
    /// <b>Not viable at 1 Gbps on this rig, and the reason is bias rather than noise.</b> The
    /// discrimination gap is 8 ns/byte - 11.6 µs across a 64-to-1518 sweep. A regression is immune
    /// to the ~490 µs software offset by construction, and jitter only costs samples. What it
    /// cannot survive is error that <i>covaries with frame size</i>, and the NPF path copies each
    /// frame four or five times plus DMA in both directions: roughly 1-4 ns/byte, which is 12-50%
    /// of the gap and biases toward a false "switch". No number of samples fixes a bias.
    /// </para>
    /// <para>
    /// Two research passes disagreed here, and the disagreement is instructive. One compared the
    /// signal against the p50 latency and its jitter, concluded it was large enough, and called
    /// this the strongest fallback. The other compared it against the per-byte software cost and
    /// concluded the opposite. The second is right, because a regression's whole virtue is
    /// discarding constant offsets - so the magnitude of the offset was never the question.
    /// </para>
    /// <para>
    /// It becomes viable at 100 Mbps, where the gap is 116 µs and the software cost is unchanged,
    /// dropping the bias to 1-5%. It also needs a probe-only mode: the current send path measures a
    /// slope of about -2,200 ns/byte, 275 times the signal and the wrong sign, because the send
    /// queue is sized in bytes and holds roughly 5,000 minimum frames against 325 full ones.
    /// </para>
    /// <para>
    /// And it is positive-only in a way the others are not: a cut-through switch adds no second
    /// serialization delay at all, so a single-hop slope is not evidence of a direct cable.
    /// </para>
    /// </remarks>
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
/// <para>
/// Deliberately coarse. A finer scale would invite arithmetic that reads as precision the physics
/// does not support - these are qualitative statements about how a standard behaves, not
/// measurements.
/// </para>
/// <para>
/// <b>Ascending, so that stronger really is greater.</b> This enum used to descend, with
/// <c>Conclusive = 0</c>, while <see cref="TopologyConfidence"/> twenty lines away ascended from
/// <c>None = 0</c>. Both were internally consistent and the comparisons against them were correct,
/// which is what made it dangerous: the next person to write the obvious
/// <c>strength &gt;= SignalStrength.Strong</c> would have inverted a verdict silently, in the one
/// direction this whole model is shaped to prevent. Two opposite conventions in one file is a trap
/// whoever set it will not be the one to spring.
/// </para>
/// </remarks>
public enum SignalStrength
{
    /// <summary>Points one way. Meaningful only alongside others.</summary>
    Suggestive,

    /// <summary>Would be surprising to be wrong about, but is not impossible.</summary>
    Strong,

    /// <summary>
    /// Settles it on its own. Reserved for signals where the alternative is physically impossible
    /// rather than merely unlikely.
    /// </summary>
    Conclusive,
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
    /// <summary>False when the signal was never attempted. See <see cref="NotRun"/>.</summary>
    public bool Ran { get; init; } = true;

    /// <summary>
    /// A signal that ran and settled nothing, which is the common case and must stay cheap to say.
    /// </summary>
    /// <remarks>
    /// The strength is <see cref="SignalStrength.Suggestive"/> because an inconclusive finding has
    /// no strength at all and something must be written there. Nothing reads it - the combiner
    /// filters inconclusive observations out before it looks at strength - but it is the weakest
    /// value on purpose, so that if that filtering is ever removed this cannot lift a verdict.
    /// </remarks>
    public static TopologyObservation Nothing(TopologySignal signal, string detail) =>
        new(signal, TopologyFinding.Inconclusive, SignalStrength.Suggestive, detail);

    /// <summary>
    /// A signal that was not run at all - declined, unavailable, or not applicable to this rig.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Nothing"/>, and the distinction is a reporting requirement rather
    /// than a nicety. Two of these signals are opt-in because they disrupt the link, so "the user
    /// declined the link-state test" and "the link-state test ran and proved nothing" are different
    /// facts about a run - and a report that cannot tell them apart is claiming coverage it does
    /// not have. RFC 2544 section 7 requires the exact configuration used, including what was
    /// disabled, to be part of the reported result.
    /// </remarks>
    public static TopologyObservation NotRun(TopologySignal signal, string why) =>
        new(signal, TopologyFinding.Inconclusive, SignalStrength.Suggestive, why) { Ran = false };
}
