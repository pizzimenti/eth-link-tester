namespace EthLinkTester.Core.Adapters;

/// <summary>
/// What a specific pair of adapters can actually test.
/// </summary>
/// <remarks>
/// The test matrix is derived from probed hardware, never hardcoded and never inferred. A rig of
/// two gigabit adapters cannot exercise 2.5GBASE-T no matter what the app supports, and showing
/// a tier the fixture cannot reach implies the result is a gap in the cable rather than in the
/// rig.
/// </remarks>
public sealed record RigCapabilities
{
    /// <summary>
    /// Speeds both adapters have positive evidence of supporting, ascending.
    /// </summary>
    /// <remarks>
    /// Derived from evidence, not from a ceiling. A maximum does not imply every tier beneath
    /// it - X550-class adapters reach 10 Gbps and have no 10BASE-T whatsoever - so inferring
    /// downwards would schedule tests the hardware cannot run.
    /// </remarks>
    public required IReadOnlyList<LinkSpeed> TestableSpeeds { get; init; }

    /// <summary>
    /// Settings that can genuinely be pinned on <em>both</em> ends with negotiation off.
    /// </summary>
    /// <remarks>
    /// Two filters apply. Forcing one end alone does not pin the link, so both adapters must
    /// offer the setting. And 802.3 Clause 40 forbids disabling negotiation at 1000BASE-T and
    /// above, so those never appear here however the driver presents them - see
    /// <see cref="AdvertisementRestrictedSettings"/>.
    /// </remarks>
    public required IReadOnlyList<SpeedDuplex> ForceableSettings { get; init; }

    /// <summary>
    /// Settings both drivers offer that look fixed but only restrict advertised capability;
    /// negotiation still runs underneath. Kept separate so the UI never calls them forcing.
    /// </summary>
    public IReadOnlyList<SpeedDuplex> AdvertisementRestrictedSettings { get; init; } = [];

    /// <summary>
    /// Highest tier the pair can negotiate, or null when either end's maximum is unknown.
    /// </summary>
    /// <remarks>
    /// Null propagates deliberately. If one adapter has no capability evidence - typically
    /// because its link is down - claiming the pair reaches the other adapter's maximum would
    /// assert something about hardware nobody has observed.
    /// </remarks>
    public LinkSpeed? MaximumMutualSpeed { get; init; }

    /// <summary>True when both ends do 10BASE-T, enabling the out-of-spec reachability probe.</summary>
    public bool SupportsLongRunProbe { get; init; }

    /// <summary>
    /// Plain-language caveats to surface in the UI and stamp on reports, so a narrow rig is
    /// never mistaken for a complete result.
    /// </summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    public static RigCapabilities Derive(
        NetworkAdapterInfo firstAdapter,
        AdapterCapabilities firstCapabilities,
        NetworkAdapterInfo secondAdapter,
        AdapterCapabilities secondCapabilities)
    {
        ArgumentNullException.ThrowIfNull(firstAdapter);
        ArgumentNullException.ThrowIfNull(firstCapabilities);
        ArgumentNullException.ThrowIfNull(secondAdapter);
        ArgumentNullException.ThrowIfNull(secondCapabilities);

        var ceiling = MutualMaximum(firstCapabilities.MaximumSpeed, secondCapabilities.MaximumSpeed);

        // Evidence from both ends, intersected. Never "everything below the ceiling".
        var testable = firstCapabilities.SupportedSpeeds
            .Where(secondCapabilities.SupportedSpeeds.Contains)
            .Where(s => ceiling is null || s <= ceiling.Value)
            .Order()
            .ToArray();

        var mutuallyOffered = firstCapabilities.ForceableSettings
            .Where(secondCapabilities.ForceableSettings.Contains)
            .Order(SpeedDuplexOrder.Instance)
            .ToArray();

        var forceable = mutuallyOffered.Where(s => s.IsTrulyForceable).ToArray();
        var advertisementOnly = mutuallyOffered.Where(s => !s.IsTrulyForceable).ToArray();

        var limitations = BuildLimitations(
            firstAdapter, firstCapabilities,
            secondAdapter, secondCapabilities,
            ceiling, forceable, advertisementOnly);

        return new RigCapabilities
        {
            TestableSpeeds = testable,
            ForceableSettings = forceable,
            AdvertisementRestrictedSettings = advertisementOnly,
            MaximumMutualSpeed = ceiling,
            SupportsLongRunProbe = testable.Contains(LinkSpeed.Mbps10),
            Limitations = limitations,
        };
    }

    private static List<string> BuildLimitations(
        NetworkAdapterInfo firstAdapter,
        AdapterCapabilities firstCapabilities,
        NetworkAdapterInfo secondAdapter,
        AdapterCapabilities secondCapabilities,
        LinkSpeed? ceiling,
        SpeedDuplex[] forceable,
        SpeedDuplex[] advertisementOnly)
    {
        var limitations = new List<string>();

        if (ceiling is null)
        {
            limitations.Add(
                "The rig's maximum speed is unknown because at least one adapter is not linked. " +
                "Connect both ends to determine what this fixture can reach.");
        }
        else if (ceiling.Value < LinkSpeed.Mbps10000)
        {
            limitations.Add(
                $"This rig tops out at {ceiling.Value.StandardName()}. Tiers above it are not " +
                "a gap in the cable and are not reported.");

            var slower = Slower(firstAdapter, firstCapabilities, secondAdapter, secondCapabilities);
            if (slower is not null)
            {
                limitations.Add($"Ceiling is set by {slower.Name} ({slower.Description}).");
            }
        }

        if (advertisementOnly.Length > 0)
        {
            limitations.Add(
                "Both drivers offer " +
                string.Join(", ", advertisementOnly) +
                " as a fixed setting, but 802.3 requires auto-negotiation at those rates. These " +
                "restrict advertised capability rather than pinning the link, and are not used " +
                "as forced-speed tests.");
        }

        if (!forceable.Any(s => s.Speed == LinkSpeed.Mbps100))
        {
            limitations.Add(
                "100BASE-TX cannot be pinned on both ends, so the 100 Mbps tier is only " +
                "observed if auto-negotiation happens to select it.");
        }

        if (!firstCapabilities.SupportsMdiControl || !secondCapabilities.SupportsMdiControl)
        {
            limitations.Add(
                "Neither adapter exposes MDI/MDI-X control. A forced 10 or 100 test that fails " +
                "to link on a direct straight-through cable is most likely an MDI artifact " +
                "rather than a cable fault, and is reported as a caveat.");
        }

        return limitations;
    }

    /// <summary>
    /// The lower of two maxima, or null when either is unknown.
    /// </summary>
    private static LinkSpeed? MutualMaximum(LinkSpeed? first, LinkSpeed? second) =>
        first is null || second is null
            ? null
            : (LinkSpeed)Math.Min((int)first.Value, (int)second.Value);

    private static NetworkAdapterInfo? Slower(
        NetworkAdapterInfo firstAdapter,
        AdapterCapabilities firstCapabilities,
        NetworkAdapterInfo secondAdapter,
        AdapterCapabilities secondCapabilities)
    {
        if (firstCapabilities.MaximumSpeed is null
            || secondCapabilities.MaximumSpeed is null
            || firstCapabilities.MaximumSpeed == secondCapabilities.MaximumSpeed)
        {
            return null;
        }

        return firstCapabilities.MaximumSpeed < secondCapabilities.MaximumSpeed
            ? firstAdapter
            : secondAdapter;
    }

    private sealed class SpeedDuplexOrder : IComparer<SpeedDuplex>
    {
        public static readonly SpeedDuplexOrder Instance = new();

        public int Compare(SpeedDuplex x, SpeedDuplex y)
        {
            var bySpeed = ((int)x.Speed).CompareTo((int)y.Speed);
            return bySpeed != 0 ? bySpeed : x.Duplex.CompareTo(y.Duplex);
        }
    }
}
