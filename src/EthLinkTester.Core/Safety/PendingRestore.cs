using System.Text.Json.Serialization;

namespace EthLinkTester.Core.Safety;

/// <summary>
/// One adapter property that was changed and must be put back.
/// </summary>
/// <remarks>
/// Written to the journal and flushed to disk <em>before</em> the change is applied. If the
/// process dies between the two, the journal describes a change that never happened - restoring
/// it is harmless. The reverse ordering would lose the original value entirely.
/// </remarks>
public sealed record PendingRestore
{
    public required string AdapterId { get; init; }

    /// <summary>Kept so a recovery message can name the adapter a person recognises.</summary>
    public required string AdapterName { get; init; }

    /// <summary>The driver's registry keyword, e.g. <c>*SpeedDuplex</c>.</summary>
    public required string PropertyKeyword { get; init; }

    /// <summary>Human-readable property name, for the same reason as AdapterName.</summary>
    public string? PropertyDisplayName { get; init; }

    /// <summary>The value to put back.</summary>
    public required string OriginalValue { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }

    public override string ToString() =>
        $"{AdapterName}: {PropertyDisplayName ?? PropertyKeyword} -> {OriginalValue}";
}

[JsonSerializable(typeof(PendingRestore))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class RestoreJournalJsonContext : JsonSerializerContext;
