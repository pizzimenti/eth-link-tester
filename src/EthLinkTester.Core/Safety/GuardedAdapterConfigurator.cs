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

    public async Task<ConfigurationOutcome> ApplyAsync(
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
        // needed to happen. Reported rather than swallowed: this is the only place that knows the
        // adapter was already in the requested state, and a probe that reads the resulting state as
        // evidence has to know that this run did not produce it.
        if (string.Equals(property.RegistryValue, registryValue, StringComparison.Ordinal))
        {
            return ConfigurationOutcome.AlreadyAtTarget;
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

        return ConfigurationOutcome.Applied;
    }

    public async Task<ConfigurationOutcome> ForceSpeedAsync(
        NetworkAdapterInfo adapter,
        SpeedDuplex setting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var property = await FindPropertyAsync(
            adapter.Id, WellKnownKeywords.SpeedDuplex, cancellationToken).ConfigureAwait(false);

        var registryValue = property.RegistryValueFor(setting)
            ?? throw new InvalidOperationException(
                $"'{adapter.Name}' does not offer {setting}. " +
                $"Available: {string.Join(", ", property.Options.Select(o => o.DisplayValue))}.");

        return await ApplyAsync(
                adapter, WellKnownKeywords.SpeedDuplex, registryValue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RestoreOutcome> RestoreAsync(
        RestoreScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var pending = await _journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false);

        if (pending.IsEmpty)
        {
            return RestoreOutcome.Empty;
        }

        // A journal whose every line is corrupt cannot be acted on, and leaving it in place is
        // actively harmful: the next append lands after a partial line and is unreadable too, so
        // one torn write would otherwise turn the journal into something that silently swallows
        // the rest of the run. Discard it and say so - adapters may still be altered, and only
        // the user can check now.
        //
        // A full pass only. Discarding is a recovery action taken when nothing in the journal can
        // be acted on; a probe putting one property back has no business throwing away records it
        // cannot read and did not write.
        if (pending.IsUnreadable && scope.IsEverything)
        {
            await _journal.DiscardAsync(cancellationToken).ConfigureAwait(false);

            return new RestoreOutcome
            {
                Restored = [],
                Failures = [],
                UnreadableRecords = pending.UnreadableLines,
                JournalCleared = true,
            };
        }

        var restored = new List<PendingRestore>();
        var abandoned = new List<PendingRestore>();
        var failures = new List<RestoreFailure>();
        var rejected = new List<RestoreFailure>();

        // One property read per adapter, not per entry. A read costs ~57 ms against the CIM
        // provider, and a run that touched several properties on one adapter paid it for each.
        var propertiesByAdapter = new Dictionary<string, IReadOnlyList<AdapterProperty>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in OriginalValues(pending.Entries).Where(scope.Includes))
        {
            try
            {
                // Validated against the driver's own options, exactly as ApplyAsync does. Restore
                // used to write the recorded value straight through, which trusted the journal
                // file's contents completely - and the journal is a file on disk, so that trust
                // was only ever as strong as its permissions. A value the driver does not offer
                // cannot be one this app recorded, whatever the file says.
                if (!propertiesByAdapter.TryGetValue(entry.AdapterId, out var properties))
                {
                    properties = await _writer.ReadPropertiesAsync(entry.AdapterId, cancellationToken)
                        .ConfigureAwait(false);
                    propertiesByAdapter[entry.AdapterId] = properties;
                }

                var property = Find(properties, entry.AdapterId, entry.PropertyKeyword);

                if (!property.Accepts(entry.OriginalValue))
                {
                    // Reported once, then dropped. Retrying cannot help - the driver will never
                    // accept this value - so keeping it would fail on every launch forever and
                    // leave the user a permanent alarm they have no way to clear. That is the same
                    // poisoned-entry trap as vanished hardware, and an entry this app did not
                    // write is precisely the one there is no reason to preserve.
                    rejected.Add(new RestoreFailure
                    {
                        Entry = entry,
                        Reason =
                            $"'{entry.OriginalValue}' is not a value {entry.PropertyKeyword} " +
                            "accepts, so the journal entry did not come from this app.",
                    });

                    abandoned.Add(entry);
                    continue;
                }

                await _writer.WriteAsync(
                    entry.AdapterId, entry.PropertyKeyword, entry.OriginalValue, cancellationToken)
                    .ConfigureAwait(false);

                restored.Add(entry);
            }
            catch (Exception ex) when (ex is AdapterNotFoundException or UnusableAdapterIdException)
            {
                // Retrying cannot help. The hardware is gone, or the entry names an adapter that
                // cannot even be addressed - a malformed id passes JSON validation but no write
                // built from it can ever succeed. Either way, keeping it would fail on every
                // launch forever and show an alarm the user has no way to clear.
                abandoned.Add(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new RestoreFailure { Entry = entry, Reason = ex.Message });
            }
        }

        // Removing an entry is the one moment its safety net comes off, so only the entries
        // genuinely dealt with are removed. Anything that failed stays for the next launch, and
        // removal is by identity so a concurrent run's new entries survive.
        var settled = restored.Concat(abandoned).ToList();
        var resolvedEntries = settled
            .SelectMany(s => pending.Entries.Where(
                e => e.AdapterId == s.AdapterId && e.PropertyKeyword == s.PropertyKeyword))
            .ToList();

        if (resolvedEntries.Count > 0)
        {
            await _journal.RemoveAsync(resolvedEntries, cancellationToken).ConfigureAwait(false);
        }

        // Read back rather than inferring. "Nothing failed" is not the same as "the journal is
        // empty": an entry recorded by a concurrent run during this pass survives removal by
        // design, and reporting the journal as clear while it still holds one would claim the
        // safety net is gone when it is not.
        var afterwards = await _journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false);

        return new RestoreOutcome
        {
            Restored = restored,
            Failures = failures,
            Abandoned = abandoned,
            Rejected = rejected,
            UnreadableRecords = pending.UnreadableLines,
            JournalCleared = afterwards.IsEmpty,
        };
    }

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

        return Find(properties, adapterId, keyword);
    }

    private static AdapterProperty Find(
        IReadOnlyList<AdapterProperty> properties, string adapterId, string keyword) =>
        properties.FirstOrDefault(
                p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Adapter '{adapterId}' has no advanced property '{keyword}'.");
}
