using EthLinkTester.Core.Preflight;
using Microsoft.Management.Infrastructure;

namespace EthLinkTester.Platform;

/// <summary>
/// Reads an adapter's bindings from the NetAdapter provider and hands them to
/// <see cref="SoftwareBridgeStatus.From"/> to interpret.
/// </summary>
/// <remarks>
/// <para>
/// The query is here and the rules are not, because deciding what a set of bindings means is
/// reasoning rather than hardware access. Only the reading needs Windows.
/// </para>
/// <para>
/// Every fact about the provider below came from running it rather than from documentation, and
/// three of them shaped the query:
/// </para>
/// <list type="bullet">
/// <item><c>InstanceID</c> is <c>{interface-guid}::componentId</c>, so bindings key off the same
/// GUID everything else in this app does. Matching on <c>InterfaceDescription</c> instead returns
/// every adapter sharing a description - which on a rig with a matched pair, the configuration this
/// tool is built for, silently doubles every row.</item>
/// <item><c>ComponentClassName</c> is exposed and carries <c>Transport</c>, <c>Filter</c>,
/// <c>Service</c> or <c>Client</c>. That turns "an unrecognised <i>protocol</i> binding" from a
/// guess about the shape of a component id into a query - and keeps Npcap's own enabled filter,
/// which is on both reference adapters, from caveating every report this tool produces.</item>
/// <item>The LBFO classes exist and answer with zero rows when no team is configured, which is what
/// makes the <c>ms_implat</c> corroboration cheap enough to insist on.</item>
/// </list>
/// </remarks>
public sealed class WindowsSoftwareBridgeProbe : ISoftwareBridgeProbe
{
    /// <summary>
    /// Reads the adapter's bindings, off the calling thread.
    /// </summary>
    /// <remarks>
    /// The CIM queries are synchronous and there is no asynchronous MI overload worth the
    /// complexity here, so the work is pushed to the pool rather than dressed up: returning
    /// <c>Task.FromResult</c> around blocking provider calls froze the UI thread for the length of
    /// four queries at the very moment the user pressed a button, and made the cancellation token a
    /// decoration. Preflight runs twice per detection, once per adapter.
    /// </remarks>
    public Task<SoftwareBridgeStatus> InspectAsync(
        string adapterId, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(adapterId, cancellationToken), cancellationToken);

    private static SoftwareBridgeStatus Inspect(
        string adapterId, CancellationToken cancellationToken)
    {
        var instanceId = Cim.ToInstanceId(adapterId);

        var bindings = Cim.QueryAllBindings(
            "SELECT ComponentID, ComponentClassName, Enabled " +
            $"FROM MSFT_NetAdapterBindingSettingData WHERE InstanceID LIKE '{instanceId}::%'");

        try
        {
            // Between the two queries, because the second is the one that can be skipped: a
            // cancellation noticed here saves the LBFO round trip.
            cancellationToken.ThrowIfCancellationRequested();

            var read = bindings
                .Select(b => new AdapterBinding(
                    Cim.Text(b, "ComponentID") ?? string.Empty,
                    Cim.Text(b, "ComponentClassName") ?? string.Empty,
                    Cim.Prop(b, "Enabled") as bool? == true))
                .ToList();

            return SoftwareBridgeStatus.From(adapterId, read, AnyTeamExists());
        }
        finally
        {
            foreach (var binding in bindings)
            {
                binding.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether any LBFO team exists on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Machine-wide rather than per-adapter, and the adapter-specific half of the test belongs to
    /// the caller: <c>ms_implat</c> has to be <i>enabled</i> on the adapter in question, and it is
    /// bound-and-disabled on every adapter of the reference machine with no team anywhere. An
    /// enabled teaming protocol plus a team that exists is the conjunction worth acting on.
    /// </para>
    /// <para>
    /// Not narrowed further because the key format of <c>MSFT_NetLbfoTeamMember</c> could not be
    /// verified here - the reference machine has no team to inspect, and matching against a shape
    /// nobody has seen is how a description-substring bug gets written.
    /// </para>
    /// <para>
    /// A throw is treated as "no team". The classes are present on every supported build and answer
    /// even with none configured, so a failure means something unusual about the machine; refusing
    /// to test would be a worse outcome than missing a team the enabled-binding check has already
    /// made unlikely.
    /// </para>
    /// </remarks>
    private static bool AnyTeamExists()
    {
        try
        {
            var members = Cim.Query("SELECT InstanceID FROM MSFT_NetLbfoTeamMember");

            try
            {
                return members.Count > 0;
            }
            finally
            {
                foreach (var member in members)
                {
                    member.Dispose();
                }
            }
        }
        catch (CimException)
        {
            return false;
        }
    }
}
