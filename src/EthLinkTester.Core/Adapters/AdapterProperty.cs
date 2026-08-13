namespace EthLinkTester.Core.Adapters;

/// <summary>One selectable value of an advanced property.</summary>
/// <remarks>
/// Drivers expose two parallel lists - registry values and the display strings that correspond to
/// them - and the pairing is positional. Keeping them together as one option means nothing
/// downstream has to index two arrays in step.
/// </remarks>
public sealed record AdapterPropertyOption
{
    /// <summary>What gets written. Numeric and locale-independent.</summary>
    public required string RegistryValue { get; init; }

    /// <summary>What the driver calls it, e.g. "100 Mbps Full Duplex".</summary>
    public required string DisplayValue { get; init; }

    public override string ToString() => DisplayValue;
}

/// <summary>
/// One advanced property of an adapter, with its current value and everything it could be set to.
/// </summary>
/// <remarks>
/// Values are written by registry value rather than display string. The display strings are
/// written for people and vary by driver, so matching on them to perform a write would be
/// guessing; the registry value is what the driver actually stores.
/// </remarks>
public sealed record AdapterProperty
{
    /// <summary>The driver's registry keyword, e.g. <c>*SpeedDuplex</c>.</summary>
    public required string Keyword { get; init; }

    /// <summary>Human-readable name, e.g. "Speed &amp; Duplex".</summary>
    public string? DisplayName { get; init; }

    public required string RegistryValue { get; init; }

    public string? DisplayValue { get; init; }

    /// <summary>The driver's own default, which is not necessarily the current value.</summary>
    public string? DefaultRegistryValue { get; init; }

    public IReadOnlyList<AdapterPropertyOption> Options { get; init; } = [];

    /// <summary>
    /// Whether a value is one the driver actually offers.
    /// </summary>
    /// <remarks>
    /// An empty option list means the property is free-form - numeric properties such as buffer
    /// counts have a min/max rather than an enumeration - so absence of options is not a reason
    /// to reject a write.
    /// </remarks>
    public bool Accepts(string registryValue) =>
        Options.Count == 0 || Options.Any(o => o.RegistryValue == registryValue);

    /// <summary>
    /// The registry value whose display string parses to <paramref name="setting"/>, or null when
    /// the driver does not offer it.
    /// </summary>
    /// <remarks>
    /// Parsing the display string is the only way across: there is no standard registry value for
    /// a given speed and duplex, and vendors do differ. Routing it through
    /// <see cref="SpeedDuplexParser"/> keeps that one piece of guesswork in a single tested place.
    /// </remarks>
    public string? RegistryValueFor(SpeedDuplex setting)
    {
        foreach (var option in Options)
        {
            if (SpeedDuplexParser.TryParse(option.DisplayValue, out var parsed) && parsed == setting)
            {
                return option.RegistryValue;
            }
        }

        return null;
    }

    public override string ToString() =>
        $"{DisplayName ?? Keyword} = {DisplayValue ?? RegistryValue}";
}
