namespace EthLinkTester.Core.Preflight;

/// <summary>
/// A software bridge stacked on an adapter, which makes the measurement path something other than
/// the cable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a topology signal.</b> This sits in preflight beside
/// <see cref="NpcapStatus"/> rather than becoming a <c>TopologyObservation</c>, because it is a
/// different kind of fact. A topology verdict answers "what is on the wire between these two
/// ports". A Hyper-V vSwitch answers "these frames may never reach a wire at all". Feeding it in as
/// a bridged observation would print "a switch or bridge is in the path" while pointing at a bare
/// cable - technically true and thoroughly misleading, which is the failure this project keeps
/// working to avoid.
/// </para>
/// <para>
/// The adapter filter cannot catch this. `HardwareInterface = TRUE AND Virtual = FALSE AND
/// NdisPhysicalMedium = 14` stays true of a physical NIC whether or not a bridge is stacked on it -
/// the bridge is a *binding* plus a separate virtual miniport, and the NIC underneath is unchanged.
/// So this is a second query, not a tighter filter.
/// </para>
/// </remarks>
public enum SoftwareBridge
{
    /// <summary>Nothing is bridging this adapter in software.</summary>
    None,

    /// <summary>
    /// Bound to a Hyper-V extensible virtual switch - component id <c>vms_pp</c>, a NetTrans
    /// protocol.
    /// </summary>
    /// <remarks>
    /// Frames traverse <c>vmswitch.sys</c>. No cable verdict is possible and no measurement from
    /// this adapter describes a link.
    /// </remarks>
    HyperVSwitch,

    /// <summary>
    /// A member of a Windows Network Bridge - component id <c>ms_bridge</c>, a NetService
    /// lightweight filter.
    /// </summary>
    /// <remarks>
    /// The case most likely to be hit by accident: "Bridge Connections" is two clicks in the
    /// Network Connections window and leaves no other visible sign. If both selected adapters are
    /// members, they are one L2 segment in software and the probe sweep will produce a fingerprint
    /// of nothing.
    /// </remarks>
    WindowsNetworkBridge,

    /// <summary>
    /// A member of a Windows LBFO team - <c>ms_implat</c> bound and enabled, corroborated by a
    /// team-member instance.
    /// </summary>
    /// <remarks>
    /// The corroboration is required rather than tidy. <c>ms_implat</c> is bound-but-disabled on
    /// every adapter of the reference machine with no team configured anywhere, so the binding
    /// alone proves nothing.
    /// </remarks>
    NetworkTeam,

    /// <summary>
    /// An unrecognised protocol-class binding, which may be a vendor teaming or bridging component.
    /// </summary>
    /// <remarks>
    /// Reported rather than refused. Enumerating every vendor's component id is a losing game and a
    /// hard refusal on an unknown binding would break against the next NIC suite; a caveat on the
    /// report is proportionate to a maybe.
    /// </remarks>
    UnrecognisedProtocolBinding,
}

/// <summary>
/// What preflight found stacked on one adapter, and what the app should do about it.
/// </summary>
/// <param name="Bridge">The condition found.</param>
/// <param name="AdapterId">The adapter it was found on.</param>
/// <param name="ComponentId">
/// The binding's component id, which is what was actually matched.
/// </param>
/// <param name="Detail">What to tell the user, naming the component and the consequence.</param>
public sealed record SoftwareBridgeStatus(
    SoftwareBridge Bridge,
    string AdapterId,
    string? ComponentId,
    string Detail)
{
    /// <summary>Nothing found, which is the ordinary case.</summary>
    public static SoftwareBridgeStatus Clear(string adapterId) =>
        new(SoftwareBridge.None, adapterId, null, "No software bridge is stacked on this adapter.");

    /// <summary>
    /// True when a run on this adapter would measure something other than the cable, and must not
    /// start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refuse loudly in the adapter picker rather than filtering the adapter out of the list. A NIC
    /// that silently disappears leaves someone hunting for hardware that is present and working -
    /// the same failure the Npcap preflight was built to avoid by naming the missing prerequisite
    /// instead of hiding the feature.
    /// </para>
    /// <para>
    /// An unrecognised protocol binding does not reach this bar. It is a maybe, and a maybe belongs
    /// on the report rather than in a refusal.
    /// </para>
    /// </remarks>
    public bool BlocksRun => Bridge
        is SoftwareBridge.HyperVSwitch
        or SoftwareBridge.WindowsNetworkBridge
        or SoftwareBridge.NetworkTeam;

    /// <summary>True when the run may proceed but the report must say what was found.</summary>
    public bool NeedsCaveat => Bridge == SoftwareBridge.UnrecognisedProtocolBinding;
}
