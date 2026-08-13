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

    /// <summary>
    /// Complete lines that could not be parsed, meaning original values are genuinely lost.
    /// </summary>
    /// <remarks>
    /// Excludes a torn final line - see <see cref="HasTornFinalLine"/> - because the two mean
    /// opposite things and only this one warrants alarming the user.
    /// </remarks>
    public required int UnreadableLines { get; init; }

    /// <summary>
    /// Whether the file ends mid-record, which is benign.
    /// </summary>
    /// <remarks>
    /// A record is journaled <em>before</em> its adapter change is applied, so a write that did
    /// not finish describes a change that never happened. Nothing is lost and there is nothing to
    /// restore. Counting it as corruption raised a red "the original values are lost, check your
    /// adapters by hand" alarm for the most ordinary crash there is, which contradicts the
    /// reasoning the repair itself is built on.
    /// </remarks>
    public bool HasTornFinalLine { get; init; }

    /// <summary>True when the file held complete records but none of them could be understood.</summary>
    public bool IsUnreadable => Entries.Count == 0 && UnreadableLines > 0;

    /// <summary>True when there is nothing recorded to act on - the normal startup case.</summary>
    public bool IsEmpty => Entries.Count == 0 && UnreadableLines == 0;

    public static JournalContents Empty { get; } = new() { Entries = [], UnreadableLines = 0 };
}
