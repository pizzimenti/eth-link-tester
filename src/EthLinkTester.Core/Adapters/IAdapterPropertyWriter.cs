namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Raw, unjournaled access to an adapter's advanced properties.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not for general use.</b> <see cref="WriteAsync"/> changes hardware with no record of the
/// previous value, so a crash immediately afterwards strands the adapter with no way to discover
/// what it used to be. The only supported consumer is
/// <c>GuardedAdapterConfigurator</c>, which records to the restore journal first.
/// </para>
/// <para>
/// It exists as a separate interface precisely so that ordering is structural rather than
/// remembered: application code depends on <see cref="IAdapterConfigurator"/>, whose sole
/// implementation cannot write without journaling. The same reasoning keeps
/// <see cref="IAdapterProvider"/> free of mutating members.
/// </para>
/// </remarks>
public interface IAdapterPropertyWriter
{
    /// <summary>Every advanced property the driver exposes, with its options.</summary>
    Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
        string adapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a property. Records nothing; the caller is responsible for durability of the
    /// previous value <em>before</em> calling this.
    /// </summary>
    Task WriteAsync(
        string adapterId,
        string keyword,
        string registryValue,
        CancellationToken cancellationToken = default);
}
