namespace EthLinkTester.Core;

/// <summary>
/// Ethernet speed tiers over twisted pair.
/// </summary>
/// <remarks>
/// The enum value is the rate in megabits per second, so conversions need no lookup table.
/// <para>
/// 10BASE-T is present but is deliberately a reachability probe rather than a performance
/// target: between roughly 100 m and 500 m it is often the only tier that still links, which
/// makes it the bottom rung of the diagnostic ladder. Without it, "degraded" and "dead"
/// collapse into a single uninformative result.
/// </para>
/// </remarks>
public enum LinkSpeed
{
    Mbps10 = 10,
    Mbps100 = 100,
    Mbps1000 = 1_000,
    Mbps2500 = 2_500,
    Mbps5000 = 5_000,
    Mbps10000 = 10_000,
}

public static class LinkSpeedExtensions
{
    public static long MegabitsPerSecond(this LinkSpeed speed) => (long)speed;

    public static long BitsPerSecond(this LinkSpeed speed) => (long)speed * 1_000_000L;

    /// <summary>The IEEE 802.3 designation, which is what a report should print.</summary>
    public static string StandardName(this LinkSpeed speed) => speed switch
    {
        LinkSpeed.Mbps10 => "10BASE-T",
        LinkSpeed.Mbps100 => "100BASE-TX",
        LinkSpeed.Mbps1000 => "1000BASE-T",
        LinkSpeed.Mbps2500 => "2.5GBASE-T",
        LinkSpeed.Mbps5000 => "5GBASE-T",
        LinkSpeed.Mbps10000 => "10GBASE-T",
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    /// <summary>Short form for dense UI, e.g. "2.5 Gbps".</summary>
    public static string ShortName(this LinkSpeed speed) => speed switch
    {
        LinkSpeed.Mbps10 => "10 Mbps",
        LinkSpeed.Mbps100 => "100 Mbps",
        LinkSpeed.Mbps1000 => "1 Gbps",
        LinkSpeed.Mbps2500 => "2.5 Gbps",
        LinkSpeed.Mbps5000 => "5 Gbps",
        LinkSpeed.Mbps10000 => "10 Gbps",
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    /// <summary>
    /// Auto-negotiation cannot be bypassed at or above 1000BASE-T: IEEE 802.3 Clause 40
    /// requires it to resolve master/slave clock roles. Drivers that appear to offer a forced
    /// 1 Gbps setting are restricting advertised capability, not disabling negotiation.
    /// </summary>
    public static bool RequiresAutoNegotiation(this LinkSpeed speed) => speed >= LinkSpeed.Mbps1000;

    /// <summary>
    /// The tier a raw bits-per-second reading corresponds to, or null when it matches none.
    /// </summary>
    /// <remarks>
    /// Deliberately exact rather than nearest-match. A reading that is not a standard rate is
    /// something this app has not seen - a driver reporting an aggregate, a virtual adapter, a
    /// value it invented - and rounding it to the closest tier would report a speed nobody
    /// measured. Unknown is the honest answer, and callers already handle it.
    /// </remarks>
    public static LinkSpeed? FromBitsPerSecond(long bitsPerSecond) =>
        bitsPerSecond <= 0
            ? null
            : Enum.GetValues<LinkSpeed>()
                  .Cast<LinkSpeed?>()
                  .FirstOrDefault(s => s!.Value.BitsPerSecond() == bitsPerSecond);
}
