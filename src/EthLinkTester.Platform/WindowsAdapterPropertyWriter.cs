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
    private const string AdvancedPropertyClass = "MSFT_NetAdapterAdvancedPropertySettingData";

    public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
        string adapterId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        var instanceId = Cim.ToInstanceId(adapterId);

        return Task.Run<IReadOnlyList<AdapterProperty>>(
            () =>
            {
                var properties = new List<AdapterProperty>();

                // Advanced properties key on "{guid}::*Keyword", so this is a prefix match rather
                // than equality.
                foreach (var property in Cim.Query(
                    $"SELECT * FROM {AdvancedPropertyClass} WHERE InstanceID LIKE '{instanceId}::%'"))
                {
                    using (property)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var keyword = Cim.Text(property, "RegistryKeyword");
                        if (string.IsNullOrEmpty(keyword))
                        {
                            continue;
                        }

                        properties.Add(new AdapterProperty
                        {
                            Keyword = keyword,
                            DisplayName = Cim.Text(property, "DisplayName"),
                            RegistryValue = Cim.Text(property, "RegistryValue") ?? string.Empty,
                            DisplayValue = Cim.Text(property, "DisplayValue"),
                            DefaultRegistryValue = Cim.Text(property, "DefaultRegistryValue"),
                            Options = PairOptions(
                                Cim.TextArray(property, "ValidRegistryValues"),
                                Cim.TextArray(property, "ValidDisplayValues")),
                        });
                    }
                }

                return properties;
            },
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
                        $"SELECT * FROM {AdvancedPropertyClass} " +
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
            $"SELECT * FROM {AdvancedPropertyClass} WHERE InstanceID LIKE '{instanceId}::%'");

        foreach (var instance in anyProperty)
        {
            instance.Dispose();
        }

        return anyProperty.Count == 0
            ? new AdapterNotFoundException(adapterId)
            : new InvalidOperationException(
                $"Adapter '{adapterId}' has no advanced property '{keyword}'.");
    }

    /// <summary>
    /// Zips the driver's two parallel valid-value lists into one option per entry.
    /// </summary>
    /// <remarks>
    /// The pairing is positional and the lists are supposed to be the same length. When they are
    /// not, the surplus is dropped rather than guessed at: an option whose display string belongs
    /// to a different registry value would let the app write one setting while telling the user it
    /// wrote another.
    /// </remarks>
    private static List<AdapterPropertyOption> PairOptions(
        string[] registryValues, string[] displayValues)
    {
        var count = Math.Min(registryValues.Length, displayValues.Length);
        var options = new List<AdapterPropertyOption>(count);

        for (var i = 0; i < count; i++)
        {
            options.Add(new AdapterPropertyOption
            {
                RegistryValue = registryValues[i],
                DisplayValue = displayValues[i],
            });
        }

        return options;
    }
}
