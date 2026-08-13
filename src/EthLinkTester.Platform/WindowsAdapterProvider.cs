using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Platform;

/// <summary>
/// Reads adapters, capabilities, and hardware counters through the NetAdapter CIM provider.
/// </summary>
/// <remarks>
/// Uses <c>root\StandardCimv2</c>, the same source the Get-NetAdapter cmdlets wrap. Field names
/// and filter values here were verified against the live provider rather than taken from
/// documentation - notably <c>PhysicalMediaType</c> is empty on this provider and the populated
/// property is <c>NdisPhysicalMedium</c>.
/// </remarks>
public sealed class WindowsAdapterProvider : IAdapterProvider
{
    private const string Namespace = @"\\.\root\StandardCimv2";

    /// <summary>NdisPhysicalMedium value for 802.3. Wi-Fi is 9, tunnels 0, Bluetooth 10.</summary>
    private const string Ndis8023 = "14";

    private const int AdminStatusDown = 2;
    private const int MediaConnected = 1;

    /// <summary>
    /// Physical Ethernet only. Excludes tunnels, WAN miniports, Wi-Fi Direct pseudo-adapters and
    /// the kernel debug adapter, all of which are present on a typical machine and none of which
    /// can carry a cable test.
    /// </summary>
    private const string PhysicalEthernetFilter =
        "HardwareInterface = TRUE AND Virtual = FALSE AND NdisPhysicalMedium = " + Ndis8023;

    public long TimestampFrequency => Stopwatch.Frequency;

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetPhysicalAdaptersAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<NetworkAdapterInfo>>(
            () =>
            {
                var defaultRouteIds = DefaultRouteInterfaceIds();

                var adapters = new List<NetworkAdapterInfo>();
                foreach (var adapter in Query($"SELECT * FROM MSFT_NetAdapter WHERE {PhysicalEthernetFilter}"))
                {
                    using (adapter)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        adapters.Add(ToAdapterInfo(adapter, defaultRouteIds));
                    }
                }

                return adapters;
            },
            cancellationToken);

    public Task<AdapterCapabilities> ProbeCapabilitiesAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        return Task.Run(
            () =>
            {
                var (name, negotiatedSpeed, maxSpeedBits, driverVersion) = ResolveAdapter(adapterId);

                var keywords = new List<string>();
                var forceable = new List<SpeedDuplex>();
                var supportsMdi = false;
                var supportsJumbo = false;

                var escaped = name.Replace("'", "''", StringComparison.Ordinal);
                foreach (var property in Query(
                    "SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData " +
                    $"WHERE Name = '{escaped}'"))
                {
                    using (property)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var keyword = Prop(property, "RegistryKeyword") as string;
                        if (string.IsNullOrEmpty(keyword))
                        {
                            continue;
                        }

                        keywords.Add(keyword);

                        if (keyword.Equals("*SpeedDuplex", StringComparison.OrdinalIgnoreCase))
                        {
                            forceable.AddRange(
                                SpeedDuplexParser.ParseAll(Prop(property, "ValidDisplayValues") as string[]));
                        }
                        else if (keyword.Contains("MDI", StringComparison.OrdinalIgnoreCase))
                        {
                            supportsMdi = true;
                        }
                        else if (keyword.Equals("*JumboPacket", StringComparison.OrdinalIgnoreCase))
                        {
                            supportsJumbo = true;
                        }
                    }
                }

                return new AdapterCapabilities
                {
                    AdapterId = adapterId,
                    ForceableSettings = forceable,
                    MaximumSpeed = MaximumSpeed(maxSpeedBits, negotiatedSpeed),
                    NegotiatedSpeed = negotiatedSpeed,
                    SupportsMdiControl = supportsMdi,
                    SupportsJumboFrames = supportsJumbo,
                    DriverVersion = driverVersion,
                    AdvancedPropertyKeywords = keywords,
                };
            },
            cancellationToken);
    }

    public Task<AdapterCounters> ReadCountersAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        return Task.Run(
            () =>
            {
                var (name, _, _, _) = ResolveAdapter(adapterId);
                var escaped = name.Replace("'", "''", StringComparison.Ordinal);

                // Timestamp before the query so the interval never understates elapsed time,
                // which would inflate a computed rate.
                var timestamp = Stopwatch.GetTimestamp();

                using var statistics = Query(
                        "SELECT * FROM MSFT_NetAdapterStatisticsSettingData " +
                        $"WHERE Name = '{escaped}'")
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        $"No statistics available for adapter '{name}'.");

                return new AdapterCounters
                {
                    AdapterId = adapterId,
                    TimestampTicks = timestamp,
                    ReceivedBytes = ToLong(Prop(statistics, "ReceivedBytes")),
                    ReceivedUnicastPackets = ToLong(Prop(statistics, "ReceivedUnicastPackets")),
                    ReceivedPacketErrors = ToLong(Prop(statistics, "ReceivedPacketErrors")),
                    ReceivedDiscardedPackets = ToLong(Prop(statistics, "ReceivedDiscardedPackets")),
                    SentBytes = ToLong(Prop(statistics, "SentBytes")),
                    SentUnicastPackets = ToLong(Prop(statistics, "SentUnicastPackets")),
                    OutboundPacketErrors = ToLong(Prop(statistics, "OutboundPacketErrors")),
                    OutboundDiscardedPackets = ToLong(Prop(statistics, "OutboundDiscardedPackets")),
                };
            },
            cancellationToken);
    }

    private static NetworkAdapterInfo ToAdapterInfo(
        ManagementBaseObject adapter, HashSet<string> defaultRouteIds)
    {
        var id = Prop(adapter, "InterfaceGuid") as string ?? string.Empty;

        return new NetworkAdapterInfo
        {
            Id = id,
            Name = Prop(adapter, "Name") as string ?? "(unnamed)",
            Description = Prop(adapter, "InterfaceDescription") as string ?? string.Empty,
            MacAddress = FormatMac(Prop(adapter, "PermanentAddress") as string),
            Status = ToStatus(adapter),
            LinkSpeedBitsPerSecond = ToLong(Prop(adapter, "Speed")),
            Duplex = Prop(adapter, "FullDuplex") is true ? DuplexMode.Full : DuplexMode.Half,
            BusType = ToBusType(Prop(adapter, "PnPDeviceID") as string),
            CarriesDefaultRoute = defaultRouteIds.Contains(id),
        };
    }

    /// <summary>
    /// Administratively disabled is distinct from unplugged, and conflating them would send the
    /// user hunting for a cable fault when the adapter is simply switched off.
    /// </summary>
    private static AdapterStatus ToStatus(ManagementBaseObject adapter)
    {
        if (ToLong(Prop(adapter, "InterfaceAdminStatus")) == AdminStatusDown)
        {
            return AdapterStatus.Disabled;
        }

        return ToLong(Prop(adapter, "MediaConnectState")) == MediaConnected
            ? AdapterStatus.Up
            : AdapterStatus.Disconnected;
    }

    private static AdapterBusType ToBusType(string? pnpDeviceId) => pnpDeviceId switch
    {
        null => AdapterBusType.Unknown,
        _ when pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) => AdapterBusType.Usb,
        _ when pnpDeviceId.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) => AdapterBusType.Pci,
        _ => AdapterBusType.Unknown,
    };

    /// <summary>
    /// The provider reports MAC addresses as unseparated hex ("18DBF24DBBEE").
    /// </summary>
    private static string FormatMac(string? permanentAddress)
    {
        if (string.IsNullOrWhiteSpace(permanentAddress) || permanentAddress.Length % 2 != 0)
        {
            return permanentAddress ?? string.Empty;
        }

        return string.Join(
            '-',
            Enumerable.Range(0, permanentAddress.Length / 2)
                      .Select(i => permanentAddress.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>
    /// How fast an adapter can go, or null when that is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// Deliberately never derived from the forceable list. 802.3 forbids forcing 1000BASE-T and
    /// above, so a gigabit adapter legitimately lists nothing over 100 Mbps - the reference
    /// Killer E2400 does exactly that. Treating that list as a ceiling would report a
    /// disconnected gigabit NIC as 100BASE-T hardware, suppress the 1 Gbps test, and blame the
    /// fixture for what is actually a cable fault.
    /// <para>
    /// <c>MaxSpeed</c> would be the right answer but is empty on the reference hardware, so with
    /// the link down there is no evidence and null is the honest result.
    /// </para>
    /// </remarks>
    private static LinkSpeed? MaximumSpeed(long maxSpeedBits, LinkSpeed? negotiated) =>
        ToLinkSpeed(maxSpeedBits) ?? negotiated;

    private static LinkSpeed? ToLinkSpeed(long bitsPerSecond) =>
        bitsPerSecond <= 0
            ? null
            : Enum.GetValues<LinkSpeed>()
                  .Cast<LinkSpeed?>()
                  .FirstOrDefault(s => s!.Value.BitsPerSecond() == bitsPerSecond);

    private static (string Name, LinkSpeed? NegotiatedSpeed, long MaxSpeedBits, string? DriverVersion) ResolveAdapter(string adapterId)
    {
        var escaped = adapterId.Replace("'", "''", StringComparison.Ordinal);

        using var adapter = Query(
                "SELECT * FROM MSFT_NetAdapter " +
                $"WHERE InterfaceGuid = '{escaped}' AND {PhysicalEthernetFilter}")
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No physical Ethernet adapter with id '{adapterId}'.");

        var negotiated = ToLinkSpeed(ToLong(Prop(adapter, "Speed")));

        return (
            Prop(adapter, "Name") as string ?? throw new InvalidOperationException("Adapter has no name."),
            negotiated,
            ToLong(Prop(adapter, "MaxSpeed")),
            Prop(adapter, "DriverVersionString") as string);
    }

    /// <summary>
    /// Interface ids carrying a default route, so the UI can warn before a run takes the user's
    /// connectivity down.
    /// </summary>
    private static HashSet<string> DefaultRouteInterfaceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var hasGateway = nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address is { } address && !address.Equals(System.Net.IPAddress.Any));

            if (hasGateway)
            {
                ids.Add(nic.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Reads a CIM property, returning null when the provider does not expose it.
    /// </summary>
    /// <remarks>
    /// Indexing a <see cref="ManagementBaseObject"/> for an absent property throws rather than
    /// returning null, and the property set genuinely varies across Windows builds and NIC
    /// drivers. Discovered the hard way: <c>MSFT_NetAdapter</c> has no <c>DriverVersion</c> - the
    /// real name is <c>DriverVersionString</c>, and PowerShell's Get-NetAdapter synthesises the
    /// friendlier one. Throwing here would crash the app on hardware we have never seen, so a
    /// missing property degrades to "unknown" instead.
    /// </remarks>
    private static object? Prop(ManagementBaseObject source, string name)
    {
        try
        {
            return source[name];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static List<ManagementObject> Query(string query)
    {
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(Namespace), new ObjectQuery(query));

        return [.. searcher.Get().Cast<ManagementObject>()];
    }

    private static long ToLong(object? value) =>
        value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
