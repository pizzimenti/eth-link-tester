namespace EthLinkTester.Core.Adapters;

/// <summary>
/// What a configuration write actually did.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a probe cannot be safe without it.</b>
/// <see cref="Topology.ForcedSpeedAsymmetrySignal"/> must know whether the force it asked for was a
/// real change or a no-op against a value that was already there - the second case means the state
/// it is about to interpret was created by something else, most likely a previous run that died
/// before restoring. The configurator has always known the answer, because it is the thing that
/// decides to skip the write; it simply returned <c>Task</c> and threw the fact away.
/// </para>
/// <para>
/// Without it there is no correct code to write. The obvious orchestration -
/// <c>await ForceSpeedAsync(...); Observe(..., forceWasApplied: true)</c> - is wrong in exactly the
/// stranded-adapter case the flag exists to catch, and the only alternative is to re-read the
/// property and duplicate the configurator's own comparison, putting one rule in two places.
/// </para>
/// </remarks>
public enum ConfigurationOutcome
{
    /// <summary>The property was changed, and its previous value is in the restore journal.</summary>
    Applied,

    /// <summary>
    /// The property already held the requested value, so nothing was written and nothing was
    /// journaled.
    /// </summary>
    /// <remarks>
    /// Not an error, and not success either. For most callers it is the same as
    /// <see cref="Applied"/> - the adapter is in the state they asked for. For anything that
    /// interprets the resulting state as evidence, it is disqualifying: the state predates the
    /// request, so nothing about it was caused by this run.
    /// </remarks>
    AlreadyAtTarget,
}
