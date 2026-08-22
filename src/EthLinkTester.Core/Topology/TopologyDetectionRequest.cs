using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// What to detect, on which pair, and how much disruption is permitted.
/// </summary>
public sealed record TopologyDetectionRequest
{
    /// <summary>The adapter the sweep is sent from.</summary>
    public required string TransmitAdapterId { get; init; }

    /// <summary>The adapter the sweep is captured on.</summary>
    public required string ReceiveAdapterId { get; init; }

    /// <summary>
    /// Whether tests that bounce the link may run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and the reason is not politeness.</b> The forced-speed probe turns
    /// auto-negotiation off, which drops the link for several seconds - and on the adapter carrying
    /// the default route that is someone's internet connection going away mid-download. It also
    /// leaves the adapter needing a restore, so a crash between the force and the restore strands
    /// it at 100 Mbps until the journal is replayed on the next launch.
    /// </para>
    /// <para>
    /// The cost of declining is real and worth stating to the user: without it, nothing in the set
    /// can positively demonstrate a direct cable, so the verdict on a healthy direct rig will be
    /// <see cref="TopologyConclusion.Unknown"/> and grading will not be attributable.
    /// </para>
    /// </remarks>
    public bool AllowDisruptive { get; init; }

    /// <summary>
    /// How long to listen for LLDP, CDP and STP. Zero skips the listen entirely.
    /// </summary>
    /// <remarks>
    /// Defaults to zero rather than to the honest window, because three minutes of listening for a
    /// signal that can only ever prove a bridge is a poor default for an interactive verdict. The
    /// orchestrator says so in the observation rather than quietly shortening it: a listen too
    /// short to be evidence reports that it was too short.
    /// </remarks>
    public TimeSpan PassiveWindow { get; init; }

    /// <summary>
    /// How long to wait for a link to come back after the speed is forced or restored.
    /// </summary>
    /// <remarks>
    /// Generous by default. Writing <c>*SpeedDuplex</c> restarts the miniport, which takes the
    /// adapter out of the CIM enumeration briefly and then brings the PHY back through a full
    /// auto-negotiation cycle; on the reference rig the USB adapter is the slow one at several
    /// seconds. A timeout that expires is not a failure - it produces an inconclusive observation
    /// saying the link did not come back, which is a true statement about the test.
    /// </remarks>
    public TimeSpan LinkSettleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often to re-read the adapters while waiting for a link to settle.
    /// </summary>
    /// <remarks>
    /// A CIM read of every physical adapter costs tens of milliseconds and a PHY takes seconds to
    /// negotiate, so polling faster than this buys nothing and much slower adds latency to the probe
    /// that is already the slow one. On the request rather than a constant because it is the only
    /// thing standing between the settle logic and a test suite: with a fixed 500 ms, covering the
    /// two-consecutive-polls rule meant a test that really waited seconds, so every test zeroed the
    /// timeout instead and the settle rules went untested entirely.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The speed the forced-speed probe pins one end to.</summary>
    /// <remarks>
    /// 100 full duplex, and it cannot usefully be anything above 100: IEEE 802.3 requires
    /// auto-negotiation at 1000BASE-T and above so the PHYs can resolve master/slave clock roles,
    /// so a driver appearing to offer a fixed gigabit is restricting advertised capability rather
    /// than pinning the link. See <see cref="SpeedDuplex.IsTrulyForceable"/>.
    /// </remarks>
    public SpeedDuplex ForceTarget { get; init; } = SpeedDuplex.Full(LinkSpeed.Mbps100);
}
