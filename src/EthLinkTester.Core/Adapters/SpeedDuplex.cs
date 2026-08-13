namespace EthLinkTester.Core.Adapters;

public enum DuplexMode
{
    Unknown,
    Half,
    Full,
}

/// <summary>
/// A speed and duplex combination, as a driver exposes it for forcing.
/// </summary>
/// <remarks>
/// Half duplex only exists below 1000BASE-T in practice; gigabit and above are full duplex only.
/// </remarks>
public readonly record struct SpeedDuplex(LinkSpeed Speed, DuplexMode Duplex)
{
    public static SpeedDuplex Full(LinkSpeed speed) => new(speed, DuplexMode.Full);

    /// <summary>
    /// True when this setting can genuinely be forced with auto-negotiation off.
    /// </summary>
    /// <remarks>
    /// IEEE 802.3 Clause 40 requires auto-negotiation at 1000BASE-T and above so the two ends can
    /// resolve master/slave clock roles, so only 10 and 100 are truly forceable. A driver that
    /// appears to offer "1.0 Gbps Full Duplex" as a fixed setting - the Realtek USB adapter on the
    /// reference rig does - is restricting *advertised capability* while still negotiating. That
    /// distinction matters: the app must not claim it disabled negotiation when it did not.
    /// </remarks>
    public bool IsTrulyForceable => !Speed.RequiresAutoNegotiation();

    public override string ToString() =>
        Duplex == DuplexMode.Unknown
            ? Speed.ShortName()
            : $"{Speed.ShortName()} {Duplex.ToString().ToLowerInvariant()} duplex";
}
