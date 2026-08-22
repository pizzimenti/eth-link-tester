using System.Buffers;
using System.Globalization;
using EthLinkTester.Core.Adapters;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace EthLinkTester.Platform;

/// <summary>
/// Shared access to the <c>root\StandardCimv2</c> NetAdapter provider.
/// </summary>
/// <remarks>
/// <para>
/// Uses MI (<see cref="CimSession"/>) rather than the legacy <c>System.Management</c> WMI API.
/// Reads work through either, but this provider is MI-hosted and rejects ModifyInstance from the
/// COM API - <c>ManagementObject.Put</c> fails with "Object or property already exists", and
/// specifying <c>PutType.UpdateOnly</c> fails with "Invalid parameter". MI is what
/// <c>Set-CimInstance</c> wraps, and it works.
/// </para>
/// <para>
/// Field names and filter values here were verified against the live provider rather than taken
/// from documentation - notably <c>PhysicalMediaType</c> is empty on this provider and the
/// populated property is <c>NdisPhysicalMedium</c>.
/// </para>
/// </remarks>
internal static class Cim
{
    public const string Namespace = @"root\StandardCimv2";

    private const string Wql = "WQL";

    /// <summary>NdisPhysicalMedium value for 802.3. Wi-Fi is 9, tunnels 0, Bluetooth 10.</summary>
    private const string Ndis8023 = "14";

    /// <summary>
    /// Physical Ethernet only. Excludes tunnels, WAN miniports, Wi-Fi Direct pseudo-adapters and
    /// the kernel debug adapter, all of which are present on a typical machine and none of which
    /// can carry a cable test.
    /// </summary>
    public const string PhysicalEthernetFilter =
        "HardwareInterface = TRUE AND Virtual = FALSE AND NdisPhysicalMedium = " + Ndis8023;

    /// <summary>Characters that would end the string literal or mean something to WQL.</summary>
    private static readonly SearchValues<char> InvalidInKeyword = SearchValues.Create("'\\%\"");

    /// <summary>
    /// One session for the process. Creating a session per query was two thirds of the cost of a
    /// counter read, and <see cref="CimSession"/> is safe to share across threads.
    /// </summary>
    private static readonly Lazy<CimSession> Session = new(
        () => CimSession.Create(computerName: null),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Validates an adapter id and returns it in the exact form the provider stores.
    /// </summary>
    /// <remarks>
    /// All three NetAdapter classes key on <c>InstanceID</c>, which is the interface GUID - so
    /// every query can be built from a value that is provably a GUID and therefore cannot carry a
    /// quote, a wildcard, or anything else meaningful to WQL. That removes the need to escape at
    /// all, which is the point: escaping quotes SQL-style by doubling them is invalid in WQL and
    /// Windows rejects the query outright, so an adapter renamed to "Brad's NIC" permanently broke
    /// every lookup.
    /// </remarks>
    public static string ToInstanceId(string adapterId) =>
        Guid.TryParse(adapterId, out var guid)
            ? guid.ToString("B").ToUpperInvariant()
            : throw UnusableAdapterIdException.ForId(adapterId, nameof(adapterId));

    /// <summary>
    /// Validates a driver registry keyword for use in a query.
    /// </summary>
    /// <remarks>
    /// Keywords come from the driver and look like <c>*SpeedDuplex</c>. The asterisk is literal in
    /// a WQL equality comparison, but a quote would not be, and this value can reach here from a
    /// caller rather than from the provider - so it is checked rather than trusted.
    /// </remarks>
    public static string ValidateKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        if (keyword.AsSpan().IndexOfAny(InvalidInKeyword) >= 0)
        {
            throw new ArgumentException(
                $"Registry keyword '{keyword}' contains characters that are not valid in one.",
                nameof(keyword));
        }

        return keyword;
    }

    /// <summary>
    /// Runs a query. Instances are <see cref="IDisposable"/> and the caller owns them.
    /// </summary>
    public static List<CimInstance> Query(string query) =>
        [.. Session.Value.QueryInstances(Namespace, Wql, query)];

    /// <summary>
    /// Runs a query with the provider's <c>AllBindings</c> custom option set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MSFT_NetAdapterBindingSettingData</c> answers a plain query with the same set the Network
    /// Connections dialog shows - ten of eighteen on the reference adapter, the missing eight being
    /// Microsoft internals like <c>ms_ndiscap</c> and <c>ms_wfplwf_lower</c>. This is the option
    /// <c>Get-NetAdapterBinding -AllBindings</c> sets, and it is what makes the answer complete.
    /// </para>
    /// <para>
    /// Verified against the live provider rather than assumed, in both directions: the plain query
    /// returns ten rows and this returns eighteen. The three bindings that block a run are all in
    /// the visible ten, so this is insurance rather than a fix - which is the reason to have it,
    /// since a vendor component that hides itself is exactly the one worth finding.
    /// </para>
    /// </remarks>
    public static List<CimInstance> QueryAllBindings(string query)
    {
        using var options = new CimOperationOptions();
        options.SetCustomOption("AllBindings", true, mustComply: false);

        return [.. Session.Value.QueryInstances(Namespace, Wql, query, options)];
    }

    /// <summary>Applies a modified instance back to the provider.</summary>
    public static void Modify(CimInstance instance) =>
        Session.Value.ModifyInstance(Namespace, instance);

    /// <summary>
    /// Reads a property, returning null when the provider does not expose it.
    /// </summary>
    /// <remarks>
    /// The property set genuinely varies across Windows builds and NIC drivers. Discovered the
    /// hard way: <c>MSFT_NetAdapter</c> has no <c>DriverVersion</c> - the real name is
    /// <c>DriverVersionString</c>, and PowerShell's Get-NetAdapter synthesises the friendlier one.
    /// A missing property degrades to "unknown" rather than crashing the app on hardware we have
    /// never seen.
    /// </remarks>
    public static object? Prop(CimInstance instance, string name) =>
        instance.CimInstanceProperties[name]?.Value;

    public static string? Text(CimInstance instance, string name) => Prop(instance, name) switch
    {
        string s => s,
        // Several properties are declared as arrays but carry a single value; RegistryValue is
        // the one that matters here.
        string[] { Length: > 0 } a => a[0],
        null => null,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    public static string[] TextArray(CimInstance instance, string name) =>
        Prop(instance, name) as string[] ?? [];

    /// <summary>
    /// Reads an integer, treating anything that will not fit in a signed 64-bit value as unknown.
    /// </summary>
    /// <remarks>
    /// Every counter and speed on this provider is <c>UInt64</c>, so a value above
    /// <see cref="long.MaxValue"/> is representable by the source and not by the destination.
    /// NDIS defines <c>NDIS_LINK_SPEED_UNKNOWN</c> as 0xFFFFFFFFFFFFFFFF for exactly the case
    /// this app cares about - a link that is down - and converting it throws.
    /// <para>
    /// It does not appear on the reference hardware. Both down-states were tested against the
    /// live rig, not just one: administratively disabled, and the cable physically pulled with the
    /// adapter still enabled. Both report <c>Speed</c> as null. So this is a guard against drivers
    /// not yet seen rather than an observed failure - kept because the cost of being wrong is that
    /// enumeration throws for every adapter on the machine, and zero is the right answer since
    /// callers already read it as "unknown".
    /// </para>
    /// </remarks>
    public static long ToLong(object? value) => value switch
    {
        null => 0,
        ulong tooLarge when tooLarge > long.MaxValue => 0,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };
}
