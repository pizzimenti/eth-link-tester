namespace EthLinkTester.Core.Preflight;

/// <summary>
/// One protocol, filter or service component bound to an adapter.
/// </summary>
/// <remarks>
/// <para>
/// A flattened <c>MSFT_NetAdapterBindingSettingData</c> row, in Core rather than Platform because
/// deciding what a set of bindings <i>means</i> is reasoning and not hardware access. Only the query
/// needs Windows; the rules about which components put frames somewhere other than the wire are the
/// interesting half and they belong where they can be tested without a NIC.
/// </para>
/// <para>
/// <see cref="ComponentClass"/> is carried because it is what separates a protocol from a capture
/// filter, and that distinction decides whether an unrecognised component is worth mentioning. The
/// reference machine has Npcap's own filter bound and enabled on both adapters; treating that as an
/// unknown protocol would put a caveat on every report this tool ever produces.
/// </para>
/// </remarks>
/// <param name="ComponentId">The driver's component id, e.g. <c>ms_tcpip</c>. What is matched.</param>
/// <param name="ComponentClass">
/// <c>Transport</c>, <c>Filter</c>, <c>Service</c> or <c>Client</c>, as the provider names them.
/// </param>
/// <param name="Enabled">Whether the binding is actually in the stack, rather than merely present.</param>
public readonly record struct AdapterBinding(
    string ComponentId,
    string ComponentClass,
    bool Enabled)
{
    /// <summary>The class name the provider gives protocol-class components.</summary>
    public const string Transport = "Transport";

    /// <summary>True for an enabled protocol-class binding, which is the only kind that can bridge.</summary>
    public bool IsEnabledTransport =>
        Enabled && string.Equals(ComponentClass, Transport, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive component-id match, which is how every comparison here is made.</summary>
    public bool Is(string componentId) =>
        string.Equals(ComponentId, componentId, StringComparison.OrdinalIgnoreCase);
}
