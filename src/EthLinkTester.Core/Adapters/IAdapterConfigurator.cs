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
    /// no-op cannot leave a restore entry behind that outlives the run.
    /// </remarks>
    Task ApplyAsync(
        NetworkAdapterInfo adapter,
        string keyword,
        string registryValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a speed and duplex, resolving it to whatever registry value this driver uses.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the driver does not offer the setting. Note that 1000BASE-T and above are
    /// never genuinely forceable - see <see cref="SpeedDuplex.IsTrulyForceable"/> - so a driver
    /// offering them is restricting advertised capability, not pinning the link.
    /// </exception>
    Task ForceSpeedAsync(
        NetworkAdapterInfo adapter,
        SpeedDuplex setting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts every journaled property back and clears the journal.
    /// </summary>
    /// <remarks>
    /// Safe to call at startup with no run in progress: a non-empty journal then is proof that a
    /// previous run died without cleaning up, and this is the recovery path.
    /// </remarks>
    Task<RestoreOutcome> RestoreAllAsync(CancellationToken cancellationToken = default);
}
