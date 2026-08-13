namespace EthLinkTester.Core.Adapters;

/// <summary>
/// What a specific pair of adapters can actually test.
/// </summary>
/// <remarks>
/// The test matrix is derived from probed hardware, never hardcoded. A rig made of two gigabit
/// adapters cannot exercise 2.5GBASE-T no matter what the app supports, and showing the user a
/// greyed-out 10G row they can never reach is worse than not showing it: it implies the result
/// is a coverage gap in the cable rather than in the fixture.
/// </remarks>
public sealed record RigCapabilities
{
    /// <summary>Speeds both adapters can reach, ascending.</summary>
    public required IReadOnlyList<LinkSpeed> TestableSpeeds { get; init; }

    /// <summary>
    /// Settings that can be pinned on <em>both</em> ends. Forcing one end alone does not pin the
    /// link - the far end keeps negotiating and the two disagree.
    /// </summary>
    public required IReadOnlyList<SpeedDuplex> ForceableSettings { get; init; }

    /// <summary>Highest tier the pair can negotiate; the ceiling for every measurement.</summary>
    public LinkSpeed? MaximumMutualSpeed { get; init; }

    /// <summary>True when both ends do 10BASE-T, enabling the out-of-spec reachability probe.</summary>
    public bool SupportsLongRunProbe { get; init; }

    /// <summary>
    /// Plain-language caveats to surface in the UI and stamp on reports, so a narrow rig is never
    /// mistaken for a complete result.
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

        var ceiling = MinimumOf(firstCapabilities.MaximumSpeed, secondCapabilities.MaximumSpeed);

        var testable = ceiling is null
            ? []
            : Enum.GetValues<LinkSpeed>().Where(s => s <= ceiling.Value).Order().ToArray();

        // A setting is only forceable for the rig when both ends offer it.
        var forceable = firstCapabilities.ForceableSettings
            .Where(secondCapabilities.ForceableSettings.Contains)
            .Order(SpeedDuplexOrder.Instance)
            .ToArray();

        var limitations = new List<string>();

        if (ceiling is not null && ceiling.Value < LinkSpeed.Mbps10000)
        {
            limitations.Add(
                $"This rig tops out at {ceiling.Value.StandardName()}. Tiers above it are not " +
                "a gap in the cable and are not reported.");
        }

        // The ceiling is set by whichever adapter is slower, and naming it saves the user
        // wondering which half of the fixture to upgrade.
        var slower = Slower(firstAdapter, firstCapabilities, secondAdapter, secondCapabilities);
        if (slower is not null)
        {
            limitations.Add($"Ceiling is set by {slower.Name} ({slower.Description}).");
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

        var longRun = firstCapabilities.ForceableSettings.Any(s => s.Speed == LinkSpeed.Mbps10)
                      && secondCapabilities.ForceableSettings.Any(s => s.Speed == LinkSpeed.Mbps10);

        if (!longRun)
        {
            limitations.Add(
                "10BASE-T is unavailable on this rig, so the long-run reachability probe cannot " +
                "run. Cables too long to link at 100 Mbps will report as dead rather than as long.");
        }

        return new RigCapabilities
        {
            TestableSpeeds = testable,
            ForceableSettings = forceable,
            MaximumMutualSpeed = ceiling,
            SupportsLongRunProbe = longRun,
            Limitations = limitations,
        };
    }

    private static LinkSpeed? MinimumOf(LinkSpeed? first, LinkSpeed? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : (LinkSpeed)Math.Min((int)first.Value, (int)second.Value);
    }

    private static NetworkAdapterInfo? Slower(
        NetworkAdapterInfo firstAdapter,
        AdapterCapabilities firstCapabilities,
        NetworkAdapterInfo secondAdapter,
        AdapterCapabilities secondCapabilities)
    {
        if (firstCapabilities.MaximumSpeed is null || secondCapabilities.MaximumSpeed is null)
        {
            return null;
        }

        if (firstCapabilities.MaximumSpeed == secondCapabilities.MaximumSpeed)
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
