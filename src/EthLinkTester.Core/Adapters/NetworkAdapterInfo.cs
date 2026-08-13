namespace EthLinkTester.Core.Adapters;

public enum AdapterStatus
{
    Unknown,

    /// <summary>Link is up.</summary>
    Up,

    /// <summary>Enabled, but nothing is connected or the far end is down.</summary>
    Disconnected,

    /// <summary>Administratively disabled. Nothing will link until it is enabled.</summary>
    Disabled,
}

/// <summary>How the adapter attaches, which caps what the fixture can carry.</summary>
public enum AdapterBusType
{
    Unknown,
    Pci,
    Usb,
}

/// <summary>
/// One physical network adapter as the app sees it.
/// </summary>
public sealed record NetworkAdapterInfo
{
    /// <summary>Stable identity across renames. The interface GUID on Windows.</summary>
    public required string Id { get; init; }

    /// <summary>The connection name a user recognises, e.g. "Ethernet 2".</summary>
    public required string Name { get; init; }

    /// <summary>The hardware description, e.g. "Realtek USB GbE Family Controller".</summary>
    public required string Description { get; init; }

    public required string MacAddress { get; init; }

    public required AdapterStatus Status { get; init; }

    /// <summary>Negotiated receive rate in bits per second; 0 when down.</summary>
    public long LinkSpeedBitsPerSecond { get; init; }

    public DuplexMode Duplex { get; init; } = DuplexMode.Unknown;

    public AdapterBusType BusType { get; init; } = AdapterBusType.Unknown;

    /// <summary>
    /// True when this adapter currently carries the default route.
    /// </summary>
    /// <remarks>
    /// Testing it is permitted, but it must be flagged loudly: forcing a speed or toggling an
    /// offload on the interface carrying your internet connection drops that connection, and if
    /// the app dies mid-run the restore journal is the only thing that puts it back.
    /// </remarks>
    public bool CarriesDefaultRoute { get; init; }

    /// <summary>The negotiated speed as a standard tier, or null if it does not map to one.</summary>
    public LinkSpeed? NegotiatedSpeed =>
        LinkSpeedBitsPerSecond <= 0
            ? null
            : Enum.GetValues<LinkSpeed>()
                  .Cast<LinkSpeed?>()
                  .FirstOrDefault(s => s!.Value.BitsPerSecond() == LinkSpeedBitsPerSecond);

    public bool IsUp => Status == AdapterStatus.Up;
}
