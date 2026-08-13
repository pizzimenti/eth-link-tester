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

    /// <summary>Highest tier the adapter can reach, whether or not it can be forced.</summary>
    public LinkSpeed? MaximumSpeed { get; init; }

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
