using System.Diagnostics;
using System.Globalization;
using Microsoft.Management.Infrastructure;
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
    private const int AdminStatusDown = 2;
    private const int MediaConnected = 1;

    public long TimestampFrequency => Stopwatch.Frequency;

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetPhysicalAdaptersAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<NetworkAdapterInfo>>(
            () =>
            {
                var defaultRouteIds = DefaultRouteInterfaceIds();

                var adapters = new List<NetworkAdapterInfo>();
                foreach (var adapter in Cim.Query($"SELECT * FROM MSFT_NetAdapter WHERE {Cim.PhysicalEthernetFilter}"))
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
        var instanceId = Cim.ToInstanceId(adapterId);

        return Task.Run(
            () =>
            {
                var (negotiatedSpeed, maxSpeedBits, driverVersion) = ResolveAdapter(instanceId);

                // The same parsed list the configurator works from. Reading the raw CIM a second
                // time here meant two parsers over one data source, kept in step by hand.
                var properties = AdvancedProperties.Read(instanceId, cancellationToken);

                var speedDuplex = properties.FirstOrDefault(
                    p => p.Keyword.Equals(WellKnownKeywords.SpeedDuplex, StringComparison.OrdinalIgnoreCase));

                return new AdapterCapabilities
                {
                    AdapterId = adapterId,
                    ForceableSettings = SpeedDuplexParser.ParseAll(
                        speedDuplex?.Options.Select(o => o.DisplayValue)),
                    MaximumSpeed = MaximumSpeed(maxSpeedBits),
                    NegotiatedSpeed = negotiatedSpeed,
                    SupportsMdiControl = properties.Any(p => WellKnownKeywords.IsMdiControl(p.Keyword)),
                    SupportsJumboFrames = properties.Any(
                        p => p.Keyword.Equals(WellKnownKeywords.JumboPacket, StringComparison.OrdinalIgnoreCase)),
                    DriverVersion = driverVersion,
                    AdvancedPropertyKeywords = [.. properties.Select(p => p.Keyword)],
                };
            },
            cancellationToken);
    }

    public Task<AdapterCounters?> ReadCountersAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        var instanceId = Cim.ToInstanceId(adapterId);

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
                using var statistics = Cim.Query(
                        "SELECT * FROM MSFT_NetAdapterStatisticsSettingData " +
                        $"WHERE InstanceID = '{instanceId}'")
                    .FirstOrDefault();

                // A disabled adapter has no statistics instance at all - verified by disabling one
                // and watching the instance disappear rather than zero out. That is a routine
                // state the user can enter at any moment, so it is a null sample to skip, not an
                // exception to crash a polling loop.
                //
                // Disabled is not the same as unplugged, and the difference is only visible here.
                // Pulling the cable with the adapter still enabled leaves the instance in place
                // and its counters readable - measured at 726,257 received bytes on a NIC with no
                // link - so an unplugged adapter keeps polling normally and only a disabled one
                // yields null.
                if (statistics is null)
                {
                    return null;
                }

                return new AdapterCounters
                {
                    AdapterId = adapterId,
                    TimestampTicks = timestamp,
                    ReceivedBytes = Cim.ToLong(Cim.Prop(statistics, "ReceivedBytes")),
                    ReceivedUnicastPackets = Cim.ToLong(Cim.Prop(statistics, "ReceivedUnicastPackets")),
                    ReceivedBroadcastPackets = Cim.ToLong(Cim.Prop(statistics, "ReceivedBroadcastPackets")),
                    ReceivedMulticastPackets = Cim.ToLong(Cim.Prop(statistics, "ReceivedMulticastPackets")),
                    ReceivedPacketErrors = Cim.ToLong(Cim.Prop(statistics, "ReceivedPacketErrors")),
                    ReceivedDiscardedPackets = Cim.ToLong(Cim.Prop(statistics, "ReceivedDiscardedPackets")),
                    SentBytes = Cim.ToLong(Cim.Prop(statistics, "SentBytes")),
                    SentUnicastPackets = Cim.ToLong(Cim.Prop(statistics, "SentUnicastPackets")),
                    OutboundPacketErrors = Cim.ToLong(Cim.Prop(statistics, "OutboundPacketErrors")),
                    OutboundDiscardedPackets = Cim.ToLong(Cim.Prop(statistics, "OutboundDiscardedPackets")),
                };
            },
            cancellationToken);
    }

    private static NetworkAdapterInfo ToAdapterInfo(
        CimInstance adapter, HashSet<string> defaultRouteIds)
    {
        var id = Cim.Prop(adapter, "InterfaceGuid") as string ?? string.Empty;
        var status = ToStatus(adapter);

        // Duplex is only meaningful on a live link. A disconnected adapter reports nothing
        // useful, and defaulting that to Half would invent a half-duplex finding - which is a
        // genuine fault signature - out of an adapter that is merely unplugged.
        var duplex = status == AdapterStatus.Up
            ? Cim.Prop(adapter, "FullDuplex") switch
            {
                true => DuplexMode.Full,
                false => DuplexMode.Half,
                _ => DuplexMode.Unknown,
            }
            : DuplexMode.Unknown;

        return new NetworkAdapterInfo
        {
            Id = id,
            Name = Cim.Prop(adapter, "Name") as string ?? "(unnamed)",
            Description = Cim.Prop(adapter, "InterfaceDescription") as string ?? string.Empty,
            MacAddress = FormatMac(Cim.Prop(adapter, "PermanentAddress") as string),
            Status = status,
            LinkSpeedBitsPerSecond = Cim.ToLong(Cim.Prop(adapter, "Speed")),
            Duplex = duplex,
            BusType = ToBusType(Cim.Prop(adapter, "PnPDeviceID") as string),
            CarriesDefaultRoute = defaultRouteIds.Contains(id),
        };
    }

    /// <summary>
    /// Administratively disabled is distinct from unplugged, and conflating them would send the
    /// user hunting for a cable fault when the adapter is simply switched off.
    /// </summary>
    private static AdapterStatus ToStatus(CimInstance adapter)
    {
        if (Cim.ToLong(Cim.Prop(adapter, "InterfaceAdminStatus")) == AdminStatusDown)
        {
            return AdapterStatus.Disabled;
        }

        return Cim.ToLong(Cim.Prop(adapter, "MediaConnectState")) == MediaConnected
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
    private static LinkSpeed? MaximumSpeed(long maxSpeedBits) =>
        LinkSpeedExtensions.FromBitsPerSecond(maxSpeedBits);

    private static (LinkSpeed? NegotiatedSpeed, long MaxSpeedBits, string? DriverVersion) ResolveAdapter(
        string instanceId)
    {
        using var adapter = Cim.Query(
                "SELECT * FROM MSFT_NetAdapter " +
                $"WHERE InstanceID = '{instanceId}' AND {Cim.PhysicalEthernetFilter}")
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No physical Ethernet adapter with id '{instanceId}'.");

        return (
            LinkSpeedExtensions.FromBitsPerSecond(Cim.ToLong(Cim.Prop(adapter, "Speed"))),
            Cim.ToLong(Cim.Prop(adapter, "MaxSpeed")),
            Cim.Prop(adapter, "DriverVersionString") as string);
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
}
