namespace EthLinkTester.Core.Safety;

/// <summary>
/// Durable record of adapter changes that have not yet been undone.
/// </summary>
/// <remarks>
/// <para>
/// The contract that makes this worth having: <see cref="RecordAsync"/> must not return until
/// the entry is on disk. Nothing may change an adapter property before its original value has
/// been recorded, because the failure this guards against - the process dying mid-run - takes
/// the in-memory copy with it.
/// </para>
/// <para>
/// The stakes are concrete. A run may target the adapter carrying the default route. If the app
/// dies after forcing it to 100 Mbps half duplex with offloads disabled, the user is left with a
/// broken network connection and no visible cause. The journal is the only thing that puts it
/// back.
/// </para>
/// <para>
/// Because entries are only removed after a successful restore, <b>a non-empty journal at
/// startup is proof of an unrestored run</b>. Recovery needs no heuristics and no guessing about
/// whether the last shutdown was clean.
/// </para>
/// </remarks>
public interface IRestoreJournal
{
    /// <summary>
    /// Durably records an original value. Must complete before the corresponding change is
    /// applied, and must not return until the entry survives process death.
    /// </summary>
    Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry not yet restored, oldest first. Non-empty means a previous run did not clean
    /// up after itself.
    /// </summary>
    Task<IReadOnlyList<PendingRestore>> ReadPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the journal. Call only after every entry has actually been restored - this is
    /// the single point where the safety net is removed.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
