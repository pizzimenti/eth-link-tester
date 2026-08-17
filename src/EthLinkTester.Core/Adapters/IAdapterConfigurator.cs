using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Changes adapter settings for a run, and puts them back afterwards.
/// </summary>
/// <remarks>
/// This is the interface application code should depend on. Every implementation records the
/// original value durably before changing anything, so an unrestored change is always recoverable
/// - see <see cref="IRestoreJournal"/> for why that ordering is the whole design.
/// </remarks>
public interface IAdapterConfigurator
{
    /// <summary>Every advanced property the driver exposes, with its options.</summary>
    Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
        string adapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a property, having first recorded its current value to the journal.
    /// </summary>
    /// <remarks>
    /// A write that would not change anything is skipped entirely rather than journaled, so a
    /// no-op cannot leave a restore entry behind that outlives the run. The return value says which
    /// of the two happened, because a caller that interprets the resulting adapter state as
    /// evidence needs to know whether this call produced it - see
    /// <see cref="ConfigurationOutcome"/>.
    /// </remarks>
    Task<ConfigurationOutcome> ApplyAsync(
        NetworkAdapterInfo adapter,
        string keyword,
        string registryValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a speed and duplex, resolving it to whatever registry value this driver uses.
    /// </summary>
    /// <returns>
    /// Whether the setting was changed or already held the requested value.
    /// <see cref="ConfigurationOutcome.AlreadyAtTarget"/> from a probe's own force is a warning:
    /// the adapter was pinned before this run touched it.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the driver does not offer the setting. Note that 1000BASE-T and above are
    /// never genuinely forceable - see <see cref="SpeedDuplex.IsTrulyForceable"/> - so a driver
    /// offering them is restricting advertised capability, not pinning the link.
    /// </exception>
    Task<ConfigurationOutcome> ForceSpeedAsync(
        NetworkAdapterInfo adapter,
        SpeedDuplex setting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts journaled properties back, and removes only what it put back.
    /// </summary>
    /// <param name="scope">
    /// Which entries this pass may touch. <see cref="RestoreScope.Everything"/> at startup and at
    /// end of run; a narrower scope for anything undoing its own change inside a run that is still
    /// going - see <see cref="RestoreScope"/> for why that distinction is load-bearing.
    /// </param>
    /// <remarks>
    /// Safe to call at startup with no run in progress: a non-empty journal then is proof that a
    /// previous run died without cleaning up, and this is the recovery path.
    /// </remarks>
    Task<RestoreOutcome> RestoreAsync(
        RestoreScope scope, CancellationToken cancellationToken = default);
}
