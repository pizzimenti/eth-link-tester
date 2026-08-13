using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Safety;

/// <summary>
/// The only supported way to change an adapter: records the original value durably, then writes.
/// </summary>
/// <remarks>
/// Wrapping the raw writer rather than extending it means application code cannot reach the
/// unjournaled path by accident - it depends on <see cref="IAdapterConfigurator"/>, and this is
/// the sole implementation.
/// </remarks>
public sealed class GuardedAdapterConfigurator : IAdapterConfigurator
{
    private readonly IAdapterPropertyWriter _writer;
    private readonly IRestoreJournal _journal;
    private readonly TimeProvider _time;

    public GuardedAdapterConfigurator(
        IAdapterPropertyWriter writer,
        IRestoreJournal journal,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(journal);

        _writer = writer;
        _journal = journal;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
        string adapterId, CancellationToken cancellationToken = default) =>
        _writer.ReadPropertiesAsync(adapterId, cancellationToken);

    public async Task ApplyAsync(
        NetworkAdapterInfo adapter,
        string keyword,
        string registryValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ArgumentNullException.ThrowIfNull(registryValue);

        var property = await FindPropertyAsync(adapter.Id, keyword, cancellationToken)
            .ConfigureAwait(false);

        if (!property.Accepts(registryValue))
        {
            throw new InvalidOperationException(
                $"'{adapter.Name}' does not offer {registryValue} for {keyword}. " +
                $"Valid values: {string.Join(", ", property.Options.Select(o => o.RegistryValue))}.");
        }

        // A write that changes nothing still leaves a journal entry behind, and that entry would
        // outlive the run and be "restored" on a later launch - reporting a recovery that never
        // needed to happen.
        if (string.Equals(property.RegistryValue, registryValue, StringComparison.Ordinal))
        {
            return;
        }

        await _journal.RecordAsync(
            new PendingRestore
            {
                AdapterId = adapter.Id,
                AdapterName = adapter.Name,
                PropertyKeyword = property.Keyword,
                PropertyDisplayName = property.DisplayName,
                OriginalValue = property.RegistryValue,
                RecordedUtc = _time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);

        await _writer.WriteAsync(adapter.Id, property.Keyword, registryValue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ForceSpeedAsync(
        NetworkAdapterInfo adapter,
        SpeedDuplex setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var property = await FindPropertyAsync(adapter.Id, SpeedDuplexKeyword, cancellationToken)
            .ConfigureAwait(false);

        var registryValue = property.RegistryValueFor(setting)
            ?? throw new InvalidOperationException(
                $"'{adapter.Name}' does not offer {setting}. " +
                $"Available: {string.Join(", ", property.Options.Select(o => o.DisplayValue))}.");

        await ApplyAsync(adapter, SpeedDuplexKeyword, registryValue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RestoreOutcome> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return RestoreOutcome.Empty;
        }

        var restored = new List<PendingRestore>();
        var failures = new List<RestoreFailure>();

        foreach (var entry in OriginalValues(pending))
        {
            try
            {
                await _writer.WriteAsync(
                    entry.AdapterId, entry.PropertyKeyword, entry.OriginalValue, cancellationToken)
                    .ConfigureAwait(false);

                restored.Add(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new RestoreFailure { Entry = entry, Reason = ex.Message });
            }
        }

        // Clearing is the one moment the safety net comes off, so it happens only when every
        // entry is genuinely back. Anything left unrestored stays journaled for the next launch.
        var cleared = failures.Count == 0;
        if (cleared)
        {
            await _journal.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RestoreOutcome
        {
            Restored = restored,
            Failures = failures,
            JournalCleared = cleared,
        };
    }

    /// <summary>The registry keyword every driver uses for forced speed and duplex.</summary>
    private const string SpeedDuplexKeyword = "*SpeedDuplex";

    /// <summary>
    /// The value each property held before this app touched it, one entry per property.
    /// </summary>
    /// <remarks>
    /// The <em>oldest</em> entry per adapter and keyword is the true original. A run that changes
    /// one property twice - stepping 100 Full then 10 Full - journals both, and replaying them in
    /// order would land on the intermediate value rather than back where it started. Later
    /// entries record values this app itself set, which is exactly what must not be restored.
    /// </remarks>
    private static IEnumerable<PendingRestore> OriginalValues(IEnumerable<PendingRestore> pending) =>
        pending
            .GroupBy(e => (e.AdapterId, e.PropertyKeyword))
            .Select(g => g.First());

    private async Task<AdapterProperty> FindPropertyAsync(
        string adapterId, string keyword, CancellationToken cancellationToken)
    {
        var properties = await _writer.ReadPropertiesAsync(adapterId, cancellationToken)
            .ConfigureAwait(false);

        return properties.FirstOrDefault(
                p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Adapter '{adapterId}' has no advanced property '{keyword}'.");
    }
}
