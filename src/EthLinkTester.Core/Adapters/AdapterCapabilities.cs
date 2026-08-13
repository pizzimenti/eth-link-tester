namespace EthLinkTester.Core.Adapters;

/// <summary>
/// What a driver actually lets us do with an adapter, discovered by probing rather than assumed.
/// </summary>
/// <remarks>
/// Probing matters because drivers differ in ways that change the test plan. On the reference
/// rig the Killer E2400 exposes only 10 and 100 half/full, while the Realtek USB adapter also
/// offers "1.0 Gbps Full Duplex" - and neither exposes MDI/MDI-X control at all.
/// </remarks>
public sealed record AdapterCapabilities
{
    public required string AdapterId { get; init; }

    /// <summary>
    /// Speed and duplex combinations the driver offers as fixed settings, in the order it
    /// reported them. Empty when the driver exposes no speed control at all.
    /// </summary>
    public required IReadOnlyList<SpeedDuplex> ForceableSettings { get; init; }

    /// <summary>
    /// Highest tier the adapter can reach, or null when that is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// Null is a real and common answer, not a failure. There is no dependable maximum-speed
    /// property - <c>MSFT_NetAdapter.MaxSpeed</c> is empty on the reference hardware - so with
    /// the link down there is no evidence at all. It must never be inferred from
    /// <see cref="ForceableSettings"/>: 802.3 forbids forcing gigabit and above, so a gigabit
    /// adapter legitimately lists nothing above 100 Mbps. Treating that as a ceiling would
    /// suppress the 1 Gbps test on a disconnected adapter and blame the fixture for what is
    /// actually a cable fault.
    /// </remarks>
    public LinkSpeed? MaximumSpeed { get; init; }

    /// <summary>
    /// The tier this adapter was last observed negotiating, when known. Positive evidence of
    /// support for a speed the forceable list cannot show.
    /// </summary>
    public LinkSpeed? NegotiatedSpeed { get; init; }

    /// <summary>
    /// Tiers there is positive evidence this adapter supports.
    /// </summary>
    /// <remarks>
    /// Evidence only, never inference. A maximum does not imply every tier beneath it: X550-class
    /// adapters reach 10 Gbps and have no 10BASE-T at all, so deriving support from a ceiling
    /// would schedule a tier the hardware cannot do.
    /// </remarks>
    public IReadOnlyList<LinkSpeed> SupportedSpeeds =>
    [
        .. ForceableSettings.Select(s => s.Speed)
            .Concat(NegotiatedSpeed is null ? [] : [NegotiatedSpeed.Value])
            .Concat(MaximumSpeed is null ? [] : [MaximumSpeed.Value])
            .Distinct()
            .Order()
    ];

    /// <summary>
    /// Whether MDI/MDI-X can be set explicitly.
    /// </summary>
    /// <remarks>
    /// Almost always false, and it creates a real diagnostic ambiguity. MDI/MDI-X is resolved
    /// during auto-negotiation, so forcing 10 or 100 disables the mechanism that decides it. On a
    /// straight-through direct connection a forced-speed test can therefore fail to link for MDI
    /// reasons rather than cable reasons, and without this control we cannot configure our way
    /// out to prove which. See <see cref="CanAttributeForcedSpeedFailure"/>.
    /// </remarks>
    public bool SupportsMdiControl { get; init; }

    public bool SupportsJumboFrames { get; init; }

    public string? DriverVersion { get; init; }

    /// <summary>Registry keywords of every advanced property, for snapshot and restore.</summary>
    public IReadOnlyList<string> AdvancedPropertyKeywords { get; init; } = [];

    /// <summary>
    /// False when a forced-speed link failure cannot be blamed on the cable with confidence.
    /// </summary>
    /// <remarks>
    /// The rule to encode: a cable that negotiates 1 Gbps happily but fails a forced 10 or 100
    /// test on a direct straight-through connection is almost certainly hitting an MDI artifact,
    /// not a cable fault. Report it as a caveat, never as a failure.
    /// </remarks>
    public bool CanAttributeForcedSpeedFailure => SupportsMdiControl;

    public bool CanForce(SpeedDuplex setting) => ForceableSettings.Contains(setting);

    /// <summary>
    /// True when the driver offers a fixed setting at or above 1000BASE-T, which it cannot
    /// literally force. Present so the UI can label it as advertisement restriction honestly.
    /// </summary>
    public bool OffersAdvertisementRestriction =>
        ForceableSettings.Any(s => s.Speed.RequiresAutoNegotiation());
}
