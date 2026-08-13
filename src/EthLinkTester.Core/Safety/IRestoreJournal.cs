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
    /// <remarks>
    /// An implementation must also leave the journal appendable afterwards. A record written
    /// after an unterminated line is unreadable, so a torn write is not self-limiting: it makes
    /// every later entry unreadable too, which turns one lost record into a journal that silently
    /// swallows the rest of the run.
    /// </remarks>
    Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything recorded and not yet restored, oldest first, plus a count of what could not be
    /// read. Anything other than <see cref="JournalContents.IsEmpty"/> means a previous run did
    /// not clean up after itself.
    /// </summary>
    Task<JournalContents> ReadPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes specific entries, leaving anything recorded since they were read.
    /// </summary>
    /// <remarks>
    /// Removal is by identity rather than "delete the file", because a restore pass takes seconds
    /// - long enough for a concurrent run to record new changes - and discarding those would
    /// strand adapters this app had just altered. This is the single point where the safety net
    /// comes off, so it must only ever come off the entries actually put back.
    /// </remarks>
    Task RemoveAsync(
        IReadOnlyList<PendingRestore> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the journal wholesale, including anything unreadable.
    /// </summary>
    /// <remarks>
    /// Only for a journal whose content cannot be parsed at all: nothing in it can be acted on,
    /// and leaving it in place makes every future record unreadable as well. Prefer
    /// <see cref="RemoveAsync"/> in every other case.
    /// </remarks>
    Task DiscardAsync(CancellationToken cancellationToken = default);
}
