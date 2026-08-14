using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Platform;

/// <summary>
/// Reads a driver's advanced properties. The single place that knows their CIM shape.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because the capability probe and the property writer were each issuing the identical
/// query and parsing the result differently. That is duplicated <em>knowledge</em>, not just
/// duplicated code, and it does not look like duplication at a glance because the two parsers
/// produced different types - which is exactly what let them drift apart unnoticed.
/// </para>
/// <para>
/// Deliberately the <b>read</b> rather than the writer. Sharing the writer would give the
/// read-only <see cref="IAdapterProvider"/> a dependency on a type that can change hardware,
/// undoing the separation that keeps unjournaled writes unreachable from application code.
/// </para>
/// </remarks>
internal static class AdvancedProperties
{
    public const string ClassName = "MSFT_NetAdapterAdvancedPropertySettingData";

    /// <summary>
    /// Every advanced property for an adapter, with its current value and options.
    /// </summary>
    /// <remarks>
    /// Advanced properties key on <c>{guid}::*Keyword</c>, so this is a prefix match rather than
    /// equality. The interface GUID is validated, and cannot contain a WQL wildcard.
    /// </remarks>
    public static List<AdapterProperty> Read(string instanceId, CancellationToken cancellationToken)
    {
        var properties = new List<AdapterProperty>();

        foreach (var property in Cim.Query(
            $"SELECT * FROM {ClassName} WHERE InstanceID LIKE '{instanceId}::%'"))
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
    }

    /// <summary>
    /// Zips the driver's two parallel valid-value lists into one option per entry.
    /// </summary>
    /// <remarks>
    /// The pairing is positional and the lists are supposed to be the same length - measured as
    /// such on every property of both reference adapters. When they are not, the surplus is
    /// dropped rather than guessed at: an option whose display string belongs to a different
    /// registry value would let the app write one setting while telling the user it wrote another.
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
