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
        var instanceId = ToInstanceId(adapterId);

        return Task.Run(
            () =>
            {
                var (negotiatedSpeed, maxSpeedBits, driverVersion) = ResolveAdapter(instanceId);

                var keywords = new List<string>();
                var forceable = new List<SpeedDuplex>();
                var supportsMdi = false;
                var supportsJumbo = false;

                // Advanced properties key on "{guid}::*Keyword", so this is a prefix match rather
                // than equality. The interface GUID cannot contain a WQL wildcard, so the
                // validated id needs no further escaping.
                foreach (var property in Query(
                    "SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData " +
                    $"WHERE InstanceID LIKE '{instanceId}::%'"))
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
                    MaximumSpeed = MaximumSpeed(maxSpeedBits),
                    NegotiatedSpeed = negotiatedSpeed,
                    SupportsMdiControl = supportsMdi,
                    SupportsJumboFrames = supportsJumbo,
                    DriverVersion = driverVersion,
                    AdvancedPropertyKeywords = keywords,
                };
            },
            cancellationToken);
    }

    public Task<AdapterCounters?> ReadCountersAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        var instanceId = ToInstanceId(adapterId);

        return Task.Run<AdapterCounters?>(
            () =>
            {
                // Timestamp before the query so the interval never understates elapsed time,
                // which would inflate a computed rate.
                var timestamp = Stopwatch.GetTimestamp();

                // Keyed on InstanceID, which is the interface GUID verbatim. Resolving the
                // adapter's name first and querying on that cost a second WMI round trip - two
                // thirds of the total - and capped a two-adapter poll near 7 Hz against a chart
                // that wants 30.
                using var statistics = Query(
                        "SELECT * FROM MSFT_NetAdapterStatisticsSettingData " +
                        $"WHERE InstanceID = '{instanceId}'")
                    .FirstOrDefault();

                // A disabled adapter has no statistics instance at all - verified by disabling one
                // and watching the instance disappear rather than zero out. That is a routine
                // state the user can enter at any moment, so it is a null sample to skip, not an
                // exception to crash a polling loop.
                if (statistics is null)
                {
                    return null;
                }

                return new AdapterCounters
                {
                    AdapterId = adapterId,
                    TimestampTicks = timestamp,
                    ReceivedBytes = ToLong(Prop(statistics, "ReceivedBytes")),
                    ReceivedUnicastPackets = ToLong(Prop(statistics, "ReceivedUnicastPackets")),
                    ReceivedBroadcastPackets = ToLong(Prop(statistics, "ReceivedBroadcastPackets")),
                    ReceivedMulticastPackets = ToLong(Prop(statistics, "ReceivedMulticastPackets")),
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

    /// <summary>
    /// Validates an adapter id and returns it in the exact form the CIM provider stores.
    /// </summary>
    /// <remarks>
    /// All three NetAdapter classes key on <c>InstanceID</c>, which is the interface GUID - so
    /// every query in this file can be built from a value that is provably a GUID and therefore
    /// cannot carry a quote, a wildcard, or anything else meaningful to WQL. That removes the
    /// need to escape at all, which is the point: the previous code escaped quotes SQL-style by
    /// doubling them, and WQL rejects that outright. Renaming an adapter to something containing
    /// an apostrophe - "Brad's NIC" - permanently broke both the capability probe and the counter
    /// read with "Invalid query".
    /// </remarks>
    private static string ToInstanceId(string adapterId) =>
        Guid.TryParse(adapterId, out var guid)
            ? guid.ToString("B").ToUpperInvariant()
            : throw new ArgumentException(
                $"Adapter id '{adapterId}' is not an interface GUID.", nameof(adapterId));

    private static NetworkAdapterInfo ToAdapterInfo(
        ManagementBaseObject adapter, HashSet<string> defaultRouteIds)
    {
        var id = Prop(adapter, "InterfaceGuid") as string ?? string.Empty;
        var status = ToStatus(adapter);

        // Duplex is only meaningful on a live link. A disconnected adapter reports nothing
        // useful, and defaulting that to Half would invent a half-duplex finding - which is a
        // genuine fault signature - out of an adapter that is merely unplugged.
        var duplex = status == AdapterStatus.Up
            ? Prop(adapter, "FullDuplex") switch
            {
                true => DuplexMode.Full,
                false => DuplexMode.Half,
                _ => DuplexMode.Unknown,
            }
            : DuplexMode.Unknown;

        return new NetworkAdapterInfo
        {
            Id = id,
            Name = Prop(adapter, "Name") as string ?? "(unnamed)",
            Description = Prop(adapter, "InterfaceDescription") as string ?? string.Empty,
            MacAddress = FormatMac(Prop(adapter, "PermanentAddress") as string),
            Status = status,
            LinkSpeedBitsPerSecond = ToLong(Prop(adapter, "Speed")),
            Duplex = duplex,
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
    /// <remarks>
    /// Notably it does <b>not</b> fall back to the negotiated speed. Negotiation is the thing
    /// under test: a degraded cable causes a low negotiation, so treating the observed rate as
    /// the hardware maximum would let a bad cable masquerade as slower hardware. The reference
    /// Killer E2400 sat at 100 Mbps on a gigabit port for exactly this reason - reading that as
    /// a 100BASE-T ceiling would drop the rig to 100, skip the gigabit test, and report a
    /// fixture limitation instead of the cable fault it actually was.
    /// <para>
    /// A hardware-ID lookup table keyed on PnPDeviceID (PCI\VEN_1969&amp;DEV_E0A1,
    /// USB\VID_0BDA&amp;PID_8153) is the intended fallback and lands with grading in Phase 6.
    /// Until then, unknown is the honest answer.
    /// </para>
    /// </remarks>
    private static LinkSpeed? MaximumSpeed(long maxSpeedBits) => ToLinkSpeed(maxSpeedBits);

    private static LinkSpeed? ToLinkSpeed(long bitsPerSecond) =>
        bitsPerSecond <= 0
            ? null
            : Enum.GetValues<LinkSpeed>()
                  .Cast<LinkSpeed?>()
                  .FirstOrDefault(s => s!.Value.BitsPerSecond() == bitsPerSecond);

    private static (LinkSpeed? NegotiatedSpeed, long MaxSpeedBits, string? DriverVersion) ResolveAdapter(
        string instanceId)
    {
        using var adapter = Query(
                "SELECT * FROM MSFT_NetAdapter " +
                $"WHERE InstanceID = '{instanceId}' AND {PhysicalEthernetFilter}")
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No physical Ethernet adapter with id '{instanceId}'.");

        return (
            ToLinkSpeed(ToLong(Prop(adapter, "Speed"))),
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

        // The collection holds an unmanaged enumerator and must be disposed in its own right;
        // leaving it to the finalizer leaked a handle and ~7 KB per call, which a 30 Hz poll
        // turns into real growth.
        using var results = searcher.Get();

        return [.. results.Cast<ManagementObject>()];
    }

    /// <summary>
    /// Reads a CIM integer, treating anything that will not fit in a signed 64-bit value as
    /// unknown.
    /// </summary>
    /// <remarks>
    /// Every counter and speed on this provider is <c>UInt64</c>, so a value above
    /// <see cref="long.MaxValue"/> is representable by the source and not by the destination.
    /// NDIS defines <c>NDIS_LINK_SPEED_UNKNOWN</c> as 0xFFFFFFFFFFFFFFFF for exactly the case
    /// this app cares about - a link that is down - and <see cref="Convert.ToInt64(object?)"/>
    /// throws on it. On the reference hardware the property comes back null instead, so this is a
    /// guard against drivers not yet seen rather than an observed failure; the cost of being
    /// wrong is that adapter enumeration throws for every adapter on the machine.
    /// Zero is the right answer because callers already read it as "unknown".
    /// </remarks>
    private static long ToLong(object? value) => value switch
    {
        null => 0,
        ulong tooLarge when tooLarge > long.MaxValue => 0,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };
}
