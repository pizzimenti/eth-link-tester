using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Topology;

/// <summary>
/// What a detection run produced: the verdict, and what happened to the adapter it borrowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The restore outcome is part of the result rather than a side effect.</b> The forced-speed
/// probe pins a port and puts it back, and <see cref="IAdapterConfigurator.RestoreAsync"/> reports a
/// refused write in <see cref="RestoreOutcome.Failures"/> rather than throwing - by design, since
/// one adapter refusing must not abandon the others. Discarding it would let detection finish, the
/// page show a verdict, and the port stay pinned at 100 Mbps with nobody told.
/// </para>
/// <para>
/// It is recoverable: the journal keeps the entry and the next launch replays it. That is not the
/// same as harmless. The port may be carrying someone's network, and "it will fix itself when you
/// restart the app" is only useful to a person who knows there is something to fix.
/// </para>
/// </remarks>
/// <param name="Verdict">What the signals concluded.</param>
/// <param name="Restore">
/// The result of putting the forced setting back, or null when nothing was forced - which is the
/// ordinary case, since the disruptive probe is opt-in.
/// </param>
public sealed record TopologyDetection(TopologyVerdict Verdict, RestoreOutcome? Restore)
{
    /// <summary>
    /// True when an adapter this run changed could not be put back, and the user must be told.
    /// </summary>
    public bool RestoreNeedsAttention => Restore is { NeedsAttention: true };

    /// <summary>What to tell them, naming the property and the remedy.</summary>
    public string? RestoreWarning => Restore switch
    {
        { Failures.Count: > 0 } outcome =>
            $"The speed setting on {outcome.Failures[0].Entry.AdapterName} could not be put back: "
            + $"{outcome.Failures[0].Reason} It is recorded, so restarting this app will try again "
            + "- but until then the adapter is still pinned to the speed the test set.",
        { NeedsAttention: true } =>
            "An adapter setting this test changed could not be confirmed as restored. Check the "
            + "Rig page after restarting the app.",
        _ => null,
    };
}
