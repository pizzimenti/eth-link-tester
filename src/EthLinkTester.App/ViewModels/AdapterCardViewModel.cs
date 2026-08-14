using System.Globalization;
using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using Microsoft.UI.Xaml;

namespace EthLinkTester.App.ViewModels;

/// <summary>
/// One adapter as the Rig page shows it.
/// </summary>
/// <remarks>
/// A plain snapshot rather than an observable: the page rebuilds these on refresh, so there is
/// nothing to notify about. Formatting lives here for the same reason it lives in
/// <see cref="LabViewModel"/> - one line per value, unit-testable, and the view stays layout.
/// </remarks>
internal sealed class AdapterCardViewModel(NetworkAdapterInfo adapter, AdapterCapabilities? capabilities)
{
    public NetworkAdapterInfo Adapter { get; } = adapter;

    /// <summary>
    /// The hardware, short enough to be a heading: "Killer E2400" rather than "Ethernet".
    /// </summary>
    public string Nickname => AdapterNickname.From(Adapter.Description, Adapter.Name);

    /// <summary>
    /// Windows' own name for the connection, kept visible on purpose.
    /// </summary>
    /// <remarks>
    /// Every other tool the user might reach for - Network Connections, Device Manager,
    /// Get-NetAdapter, netsh - identifies the port by this name. Showing only the hardware
    /// nickname would make the app the one place that cannot be cross-referenced with the rest of
    /// the machine, which is the opposite of helpful when someone is chasing a fault.
    /// </remarks>
    public string ConnectionName => Adapter.Name;

    public string Description => Adapter.Description;

    public string MacAddress => Adapter.MacAddress;

    public string Bus => Adapter.BusType.ToString().ToUpperInvariant();

    /// <summary>
    /// Status as text, never as colour alone. A verdict distinguishable only by hue is invisible
    /// to a meaningful share of users and disappears entirely in high contrast.
    /// </summary>
    public string Status => Adapter.Status switch
    {
        AdapterStatus.Up => "Linked",
        AdapterStatus.Disconnected => "No link",
        AdapterStatus.Disabled => "Disabled",
        _ => "Unknown",
    };

    public string StatusGlyph => Adapter.Status switch
    {
        AdapterStatus.Up => "",           // checkmark
        AdapterStatus.Disconnected => "", // warning
        AdapterStatus.Disabled => "",     // cancel
        _ => "",                          // unknown
    };

    /// <summary>
    /// The negotiated speed, or an em dash when there is no link.
    /// </summary>
    /// <remarks>
    /// Deliberately not "0 Mbps". A zero reads as a measurement of a working link that is moving
    /// nothing, which is a completely different fault from having no link at all.
    /// </remarks>
    public string Speed => Adapter.Status == AdapterStatus.Up && Adapter.LinkSpeedBitsPerSecond > 0
        ? FormatBitsPerSecond(Adapter.LinkSpeedBitsPerSecond)
        : "—";

    public string Duplex => Adapter.Duplex switch
    {
        DuplexMode.Full => "Full duplex",
        DuplexMode.Half => "Half duplex",
        _ => "—",
    };

    /// <summary>
    /// Half duplex on a modern link is a fault signature in its own right - almost always a
    /// negotiation failure - so it is called out rather than merely displayed.
    /// </summary>
    public Visibility HalfDuplexWarningVisibility =>
        Adapter.Duplex == DuplexMode.Half ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DefaultRouteVisibility =>
        Adapter.CarriesDefaultRoute ? Visibility.Visible : Visibility.Collapsed;

    public string SupportedSpeeds => capabilities is null || capabilities.SupportedSpeeds.Count == 0
        ? "Not probed"
        : string.Join(", ", capabilities.SupportedSpeeds.Select(s => s.ShortName()));

    public string ForceableSettings => capabilities is null || capabilities.ForceableSettings.Count == 0
        ? "None - this driver exposes no fixed speed setting"
        : string.Join(", ", capabilities.ForceableSettings.Select(s => s.ToString()));

    public string DriverVersion => capabilities?.DriverVersion ?? "Unknown";

    public string AdvancedPropertyCount => capabilities is null
        ? "—"
        : capabilities.AdvancedPropertyKeywords.Count.ToString(CultureInfo.InvariantCulture);

    private static string FormatBitsPerSecond(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? (bitsPerSecond / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps"
        : (bitsPerSecond / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";
}
