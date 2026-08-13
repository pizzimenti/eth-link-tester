namespace EthLinkTester.Core.Safety;

/// <summary>
/// Everything a journal read found, including what it could not read.
/// </summary>
/// <remarks>
/// The unreadable count exists because "no entries" and "no readable entries" demand opposite
/// responses, and a bare list cannot tell them apart. An empty journal is the normal startup case
/// and must be left alone; a journal whose every line is corrupt is a machine that may still have
/// forced adapters and no way to discover which - and it must be repaired, because otherwise every
/// subsequent append lands after a partial line and is itself unreadable. Conflating the two turns
/// one torn write into a journal that silently swallows everything written to it afterwards.
/// </remarks>
public sealed record JournalContents
{
    public required IReadOnlyList<PendingRestore> Entries { get; init; }

    /// <summary>Lines present in the file that could not be parsed.</summary>
    public required int UnreadableLines { get; init; }

    /// <summary>True when the file held content but none of it could be understood.</summary>
    public bool IsUnreadable => Entries.Count == 0 && UnreadableLines > 0;

    /// <summary>True when there is genuinely nothing recorded - the normal startup case.</summary>
    public bool IsEmpty => Entries.Count == 0 && UnreadableLines == 0;

    public static JournalContents Empty { get; } = new() { Entries = [], UnreadableLines = 0 };
}
