using Microsoft.Management.Infrastructure;
using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Platform;

/// <summary>
/// Reads and writes advanced properties through the NetAdapter CIM provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are unjournaled.</b> Use <c>GuardedAdapterConfigurator</c>, which records the
/// original value durably first - see <see cref="IAdapterPropertyWriter"/>.
/// </para>
/// <para>
/// <c>MSFT_NetAdapterAdvancedPropertySettingData</c> exposes only a <c>Reset</c> method, so a
/// write is a CIM ModifyInstance: set <c>RegistryValue</c> on the instance and put it back. The
/// registry value is written rather than the display string because the display strings are
/// written for people and differ between drivers, while the registry value is what the driver
/// actually stores.
/// </para>
/// <para>
/// Writing <c>*SpeedDuplex</c> makes the driver reset the PHY, so the link drops and re-negotiates
/// over the following seconds. That is the intended behaviour - it is the mechanism under test -
/// but it means a caller must wait for the link rather than reading the speed immediately.
/// </para>
/// </remarks>
public sealed class WindowsAdapterPropertyWriter : IAdapterPropertyWriter
{
    public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        var instanceId = Cim.ToInstanceId(adapterId);

        return Task.Run<IReadOnlyList<AdapterProperty>>(
            () => AdvancedProperties.Read(instanceId, cancellationToken),
            cancellationToken);
    }

    public Task WriteAsync(
        string adapterId,
        string keyword,
        string registryValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(registryValue);

        var instanceId = Cim.ToInstanceId(adapterId);
        var validated = Cim.ValidateKeyword(keyword);

        return Task.Run(
            () =>
            {
                using var property = Cim.Query(
                        $"SELECT * FROM {AdvancedProperties.ClassName} " +
                        $"WHERE InstanceID = '{instanceId}::{validated}'")
                    .FirstOrDefault()
                    ?? throw MissingProperty(adapterId, instanceId, validated);

                cancellationToken.ThrowIfCancellationRequested();

                // Declared as an array by the schema even though every driver seen carries a
                // single element.
                property.CimInstanceProperties["RegistryValue"].Value = new[] { registryValue };
                Cim.Modify(property);
            },
            cancellationToken);
    }

    /// <summary>
    /// Explains a missing property: is the adapter gone, or just this setting?
    /// </summary>
    /// <remarks>
    /// The distinction decides whether a restore entry is worth retrying. A USB adapter that has
    /// been unplugged will never come back by trying again, so its entry must be abandoned rather
    /// than left to fail on every launch forever. Costs an extra query, but only on a path that
    /// has already failed.
    /// </remarks>
    private static Exception MissingProperty(string adapterId, string instanceId, string keyword)
    {
        var anyProperty = Cim.Query(
            $"SELECT * FROM {AdvancedProperties.ClassName} WHERE InstanceID LIKE '{instanceId}::%'");

        foreach (var instance in anyProperty)
        {
            instance.Dispose();
        }

        return anyProperty.Count == 0
            ? AdapterNotFoundException.ForAdapter(adapterId)
            : new InvalidOperationException(
                $"Adapter '{adapterId}' has no advanced property '{keyword}'.");
    }

}
