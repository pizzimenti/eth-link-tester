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
/// partial restore is still better than none. The journal survives whenever anything failed, so
/// the next launch tries again.
/// </remarks>
public sealed record RestoreOutcome
{
    public required IReadOnlyList<PendingRestore> Restored { get; init; }

    public required IReadOnlyList<RestoreFailure> Failures { get; init; }

    /// <summary>
    /// Whether the journal was discarded. False when anything failed, so the safety net is only
    /// ever removed once it is genuinely no longer needed.
    /// </summary>
    public required bool JournalCleared { get; init; }

    /// <summary>True when there was nothing to do, which is the normal startup case.</summary>
    public bool NothingToDo => Restored.Count == 0 && Failures.Count == 0;

    public static RestoreOutcome Empty { get; } = new()
    {
        Restored = [],
        Failures = [],
        JournalCleared = false,
    };
}
