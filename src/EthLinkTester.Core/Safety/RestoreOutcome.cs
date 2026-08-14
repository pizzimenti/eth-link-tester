namespace EthLinkTester.Core.Safety;

/// <summary>An entry that could not be put back, and why.</summary>
public sealed record RestoreFailure
{
    public required PendingRestore Entry { get; init; }

    public required string Reason { get; init; }

    public override string ToString() => $"{Entry} failed: {Reason}";
}

/// <summary>
/// The result of a restore pass.
/// </summary>
/// <remarks>
/// Failures are reported rather than thrown because one adapter refusing a write must not abandon
/// the others - the whole point is to leave the machine in the state it was found in, and a
/// partial restore is still better than none. The journal keeps whatever failed, so the next
/// launch tries again.
/// </remarks>
public sealed record RestoreOutcome
{
    public required IReadOnlyList<PendingRestore> Restored { get; init; }

    public required IReadOnlyList<RestoreFailure> Failures { get; init; }

    /// <summary>
    /// Entries whose adapter no longer exists, dropped rather than retried.
    /// </summary>
    /// <remarks>
    /// A USB adapter that has been unplugged cannot be restored and never will be by retrying.
    /// Left in the journal it would fail on every launch forever, showing a permanent error the
    /// user has no way to clear - so it is reported once and discarded. Nothing is lost: there is
    /// no hardware left to put anything back on.
    /// </remarks>
    public IReadOnlyList<PendingRestore> Abandoned { get; init; } = [];

    /// <summary>
    /// Journal records that could not be parsed, so their original values are gone.
    /// </summary>
    /// <remarks>
    /// This is the one outcome the app cannot fix for the user, and it must be stated plainly
    /// rather than folded into a count of successes: adapters may still be altered, and the only
    /// remaining remedy is to check them by hand.
    /// </remarks>
    public int UnreadableRecords { get; init; }

    /// <summary>
    /// Whether every entry is now off the journal. False when anything failed, so the safety net
    /// is only ever removed once it is genuinely no longer needed.
    /// </summary>
    public required bool JournalCleared { get; init; }

    /// <summary>True when there was nothing to do, which is the normal startup case.</summary>
    public bool NothingToDo =>
        Restored.Count == 0 && Failures.Count == 0 && Abandoned.Count == 0 && UnreadableRecords == 0;

    /// <summary>True when the user must be told something went wrong rather than merely informed.</summary>
    public bool NeedsAttention => Failures.Count > 0 || UnreadableRecords > 0;

    public static RestoreOutcome Empty { get; } = new()
    {
        Restored = [],
        Failures = [],
        JournalCleared = false,
    };
}
