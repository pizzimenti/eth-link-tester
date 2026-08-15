using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Tests;

public class GuardedAdapterConfiguratorTests
{
    private const string Keyword = "*SpeedDuplex";

    private static NetworkAdapterInfo Adapter(string id = "adapter", string name = "Ethernet") => new()
    {
        Id = id,
        Name = name,
        Description = "test adapter",
        MacAddress = "00-00-00-00-00-00",
        Status = AdapterStatus.Up,
    };

    /// <summary>
    /// A writer and a journal sharing one log, so the ordering between them is observable. That
    /// ordering is the entire contract: a value recorded after the change is a value already lost.
    /// </summary>
    private sealed class Rig : IAdapterPropertyWriter, IRestoreJournal
    {
        public List<string> Log { get; } = [];

        public List<PendingRestore> Entries { get; } = [];

        public Dictionary<string, string> Values { get; } = new() { [Keyword] = "0" };

        public HashSet<string> FailWritesFor { get; } = [];

        /// <summary>Keywords whose adapter is to look permanently gone rather than merely broken.</summary>
        public HashSet<string> MissingAdapterFor { get; } = [];

        /// <summary>Keywords whose adapter id cannot be addressed at all.</summary>
        public HashSet<string> UnusableIdFor { get; } = [];

        /// <summary>Simulates a concurrent run appending while a restore pass is under way.</summary>
        public bool RecordDuringRestore { get; set; }

        /// <summary>Set to make the journal itself fail, which must stop the adapter write.</summary>
        public bool JournalIsBroken { get; set; }

        public int UnreadableLines { get; set; }

        public bool Cleared { get; private set; }

        public bool Discarded { get; private set; }

        /// <summary>The Killer E2400's actual list: nothing above 100 Mbps.</summary>
        private static readonly AdapterPropertyOption[] SpeedDuplexOptions =
        [
            new() { RegistryValue = "0", DisplayValue = "Auto Negotiation" },
            new() { RegistryValue = "1", DisplayValue = "10 Mbps Half Duplex" },
            new() { RegistryValue = "2", DisplayValue = "10 Mbps Full Duplex" },
            new() { RegistryValue = "3", DisplayValue = "100 Mbps Half Duplex" },
            new() { RegistryValue = "4", DisplayValue = "100 Mbps Full Duplex" },
        ];

        private static readonly AdapterPropertyOption[] GenericOptions =
        [
            new() { RegistryValue = "0", DisplayValue = "Disabled" },
            new() { RegistryValue = "3", DisplayValue = "Rx & Tx Enabled" },
        ];

        public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
            string adapterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdapterProperty>>(
            [
                .. Values.Select(pair => new AdapterProperty
                {
                    Keyword = pair.Key,
                    DisplayName = pair.Key == Keyword ? "Speed & Duplex" : pair.Key,
                    RegistryValue = pair.Value,
                    DefaultRegistryValue = "0",
                    Options = pair.Key == Keyword ? SpeedDuplexOptions : GenericOptions,
                }),
            ]);

        public Task WriteAsync(
            string adapterId, string keyword, string registryValue,
            CancellationToken cancellationToken = default)
        {
            if (MissingAdapterFor.Contains(keyword))
            {
                throw AdapterNotFoundException.ForAdapter(adapterId);
            }

            if (UnusableIdFor.Contains(keyword))
            {
                throw UnusableAdapterIdException.ForId(adapterId, nameof(adapterId));
            }

            if (FailWritesFor.Contains(keyword))
            {
                throw new InvalidOperationException("driver refused the write");
            }

            Log.Add($"write {keyword}={registryValue}");
            Values[keyword] = registryValue;
            return Task.CompletedTask;
        }

        public Task RecordAsync(PendingRestore entry, CancellationToken cancellationToken = default)
        {
            if (JournalIsBroken)
            {
                throw new IOException("journal is unwritable");
            }

            Log.Add($"journal {entry.PropertyKeyword}={entry.OriginalValue}");
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<JournalContents> ReadPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalContents
            {
                Entries = [.. Entries],
                UnreadableLines = UnreadableLines,
            });

        public Task RemoveAsync(
            IReadOnlyList<PendingRestore> entries, CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                Entries.Remove(entry);
            }

            if (RecordDuringRestore)
            {
                RecordDuringRestore = false;
                Entries.Add(new PendingRestore
                {
                    AdapterId = "adapter",
                    AdapterName = "Ethernet",
                    PropertyKeyword = "*FlowControl",
                    OriginalValue = "3",
                    RecordedUtc = DateTimeOffset.UnixEpoch,
                });
            }

            Cleared = Entries.Count == 0;
            return Task.CompletedTask;
        }

        public Task DiscardAsync(CancellationToken cancellationToken = default)
        {
            Discarded = true;
            Cleared = true;
            Entries.Clear();
            UnreadableLines = 0;
            return Task.CompletedTask;
        }
    }

    private static (Rig Rig, GuardedAdapterConfigurator Configurator) Build()
    {
        var rig = new Rig();
        return (rig, new GuardedAdapterConfigurator(rig, rig));
    }

    /// <summary>
    /// The contract the whole design rests on: the original value must be durable before the
    /// change is applied. The reverse order loses it exactly when the process dies between them.
    /// </summary>
    [Fact]
    public async Task JournalsTheOriginalValueBeforeWriting()
    {
        var (rig, configurator) = Build();

        await configurator.ApplyAsync(Adapter(), Keyword, "4");

        Assert.Equal(["journal *SpeedDuplex=0", "write *SpeedDuplex=4"], rig.Log);
    }

    [Fact]
    public async Task ForcingASpeedResolvesTheDriversRegistryValue()
    {
        var (rig, configurator) = Build();

        await configurator.ForceSpeedAsync(Adapter(), new SpeedDuplex(LinkSpeed.Mbps100, DuplexMode.Full));

        Assert.Equal("4", rig.Values[Keyword]);
    }

    [Fact]
    public async Task RejectsASettingTheDriverDoesNotOffer()
    {
        var (_, configurator) = Build();

        // The reference Killer E2400 offers nothing above 100 Mbps, which is the common case.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => configurator.ForceSpeedAsync(Adapter(), SpeedDuplex.Full(LinkSpeed.Mbps1000)));
    }

    [Fact]
    public async Task RejectsARegistryValueOutsideTheDriversOptions()
    {
        var (_, configurator) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => configurator.ApplyAsync(Adapter(), Keyword, "99"));
    }

    /// <summary>
    /// A write that changes nothing must not journal. The entry would outlive the run and be
    /// "restored" on a later launch, reporting a recovery that never needed to happen.
    /// </summary>
    [Fact]
    public async Task DoesNotJournalAWriteThatChangesNothing()
    {
        var (rig, configurator) = Build();

        await configurator.ApplyAsync(Adapter(), Keyword, "0");

        Assert.Empty(rig.Log);
        Assert.Empty(rig.Entries);
    }

    [Fact]
    public async Task RestoresAndClearsTheJournal()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");

        var outcome = await configurator.RestoreAllAsync();

        Assert.Equal("0", rig.Values[Keyword]);
        Assert.True(outcome.JournalCleared);
        Assert.Single(outcome.Restored);
        Assert.Empty(outcome.Failures);
    }

    /// <summary>
    /// Regression: with a property changed twice, replaying entries in order lands on the
    /// intermediate value rather than back where it started. Only the oldest entry per property
    /// holds a value this app did not itself set.
    /// </summary>
    [Fact]
    public async Task RestoresTheValueFromBeforeTheRunNotTheIntermediateOne()
    {
        var (rig, configurator) = Build();
        var adapter = Adapter();

        await configurator.ApplyAsync(adapter, Keyword, "4");   // auto -> 100 Full
        await configurator.ApplyAsync(adapter, Keyword, "2");   // 100 Full -> 10 Full

        Assert.Equal(2, rig.Entries.Count);

        await configurator.RestoreAllAsync();

        Assert.Equal("0", rig.Values[Keyword]);
    }

    /// <summary>
    /// Regression, and the one branch of the restore path that had no test at all: a property
    /// forced twice leaves two journal entries, and removing only the one written back leaves the
    /// later one behind. The next launch would treat that leftover - a value this app itself set -
    /// as the original, making the intermediate setting permanent.
    /// </summary>
    [Fact]
    public async Task RestoringAPropertyForcedTwiceRemovesEveryEntryForIt()
    {
        var (rig, configurator) = Build();
        var adapter = Adapter();

        await configurator.ApplyAsync(adapter, Keyword, "4");   // auto -> 100 Full
        await configurator.ApplyAsync(adapter, Keyword, "2");   // 100 Full -> 10 Full
        Assert.Equal(2, rig.Entries.Count);

        await configurator.RestoreAllAsync();

        Assert.Equal("0", rig.Values[Keyword]);
        Assert.Empty(rig.Entries);
    }

    /// <summary>
    /// An entry recorded by a concurrent run during the pass survives removal by design, so the
    /// journal is not clear - and saying it is would claim the safety net is gone while it is
    /// still holding a setting nobody has put back.
    /// </summary>
    [Fact]
    public async Task DoesNotClaimTheJournalIsClearWhileAnEntryRemains()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");

        // Recorded after the pass read the journal, as a concurrent run would.
        rig.RecordDuringRestore = true;

        var outcome = await configurator.RestoreAllAsync();

        Assert.Single(outcome.Restored);
        Assert.False(outcome.JournalCleared);
        Assert.NotEmpty(rig.Entries);
    }

    /// <summary>
    /// An entry naming an adapter that cannot even be addressed can never be restored, so it must
    /// be abandoned like vanished hardware rather than retried on every launch forever.
    /// </summary>
    [Fact]
    public async Task AnEntryWithAnUnusableAdapterIdIsAbandoned()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");
        rig.UnusableIdFor.Add(Keyword);

        var outcome = await configurator.RestoreAllAsync();

        Assert.Empty(outcome.Failures);
        Assert.Single(outcome.Abandoned);
        Assert.True(outcome.JournalCleared);
    }

    /// <summary>
    /// Security regression: the journal is a file on disk, so restore's trust in it is only as
    /// strong as the file's permissions - and under %ProgramData% every standard user could append
    /// to it. A value the driver does not offer cannot be one this app recorded, whatever the file
    /// says, so restore validates against the driver's options exactly as apply does.
    /// </summary>
    [Fact]
    public async Task RefusesToRestoreAValueTheDriverDoesNotOffer()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");

        // What an appended entry looks like: well-formed, and naming a value no driver offers.
        rig.Entries[0] = rig.Entries[0] with { OriginalValue = "99" };

        var outcome = await configurator.RestoreAllAsync();

        Assert.Empty(outcome.Restored);
        Assert.Empty(outcome.Failures);

        // Its own category, not a failure: a failure implies a retry, and this can never succeed.
        var rejection = Assert.Single(outcome.Rejected);
        Assert.Contains("did not come from this app", rejection.Reason, StringComparison.Ordinal);
        Assert.True(outcome.NeedsAttention);

        // The adapter keeps the value this app set; the crafted one is never written.
        Assert.Equal("4", rig.Values[Keyword]);

        // And it is dropped rather than left to alarm every future launch.
        Assert.Single(outcome.Abandoned);
        Assert.Empty(rig.Entries);
    }

    /// <summary>
    /// Clearing is the one moment the safety net comes off, so a failed restore must keep the
    /// journal for the next launch to retry.
    /// </summary>
    [Fact]
    public async Task KeepsTheJournalWhenARestoreFails()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");
        rig.FailWritesFor.Add(Keyword);

        var outcome = await configurator.RestoreAllAsync();

        Assert.False(outcome.JournalCleared);
        Assert.False(rig.Cleared);
        Assert.Empty(outcome.Restored);
        Assert.Single(outcome.Failures);
        Assert.Contains("driver refused", outcome.Failures[0].Reason);
    }

    /// <summary>
    /// Regression, caught by mutation: changing the clear condition from "nothing failed" to
    /// "something succeeded" kept all 113 tests green, because no test had a pass and a failure
    /// in the same pass. That mutation would remove the safety net from an entry still forced.
    /// </summary>
    [Fact]
    public async Task OneFailureKeepsTheJournalEvenWhenAnotherEntrySucceeded()
    {
        var (rig, configurator) = Build();
        rig.Values["*FlowControl"] = "3";

        await configurator.ApplyAsync(Adapter(), Keyword, "4");
        await configurator.ApplyAsync(Adapter(), "*FlowControl", "0");
        rig.FailWritesFor.Add("*FlowControl");

        var outcome = await configurator.RestoreAllAsync();

        Assert.Single(outcome.Restored);
        Assert.Single(outcome.Failures);
        Assert.False(outcome.JournalCleared);

        // The entry that failed must still be recorded; the one that succeeded must not.
        Assert.Single(rig.Entries);
        Assert.Equal("*FlowControl", rig.Entries[0].PropertyKeyword);
    }

    /// <summary>
    /// Regression, caught by mutation: swallowing a journal failure kept every test green while
    /// destroying the write-ahead guarantee outright. If the original value cannot be recorded,
    /// the adapter must not be touched - an unrecorded change is an unrecoverable one.
    /// </summary>
    [Fact]
    public async Task AFailedJournalWriteStopsTheAdapterWrite()
    {
        var (rig, configurator) = Build();
        rig.JournalIsBroken = true;

        await Assert.ThrowsAsync<IOException>(
            () => configurator.ApplyAsync(Adapter(), Keyword, "4"));

        Assert.Empty(rig.Log);
        Assert.Equal("0", rig.Values[Keyword]);
    }

    /// <summary>
    /// An entry whose adapter has been unplugged can never be restored. Retrying it forever would
    /// show the user a permanent alarm about hardware they have already removed, so it is reported
    /// once and dropped - nothing is lost, because there is no hardware left to restore.
    /// </summary>
    [Fact]
    public async Task AnEntryForVanishedHardwareIsAbandonedRatherThanRetriedForever()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");
        rig.MissingAdapterFor.Add(Keyword);

        var outcome = await configurator.RestoreAllAsync();

        Assert.Empty(outcome.Failures);
        Assert.Single(outcome.Abandoned);
        Assert.True(outcome.JournalCleared);
        Assert.Empty(rig.Entries);
    }

    /// <summary>
    /// Regression for the worst defect found in review: a journal whose every line is corrupt was
    /// indistinguishable from an empty one, so it was never cleared - and because appends land
    /// after the unterminated line, every later entry became unreadable too. One torn write turned
    /// the journal into a black hole while the app reported nothing wrong.
    /// </summary>
    [Fact]
    public async Task AnUnreadableJournalIsDiscardedAndReportedRatherThanIgnored()
    {
        var (rig, configurator) = Build();
        rig.UnreadableLines = 3;

        var outcome = await configurator.RestoreAllAsync();

        Assert.False(outcome.NothingToDo);
        Assert.True(outcome.NeedsAttention);
        Assert.Equal(3, outcome.UnreadableRecords);
        Assert.True(rig.Discarded);
    }

    /// <summary>
    /// Readable entries alongside a corrupt one must still be restored, and the corruption still
    /// reported - the user has adapters that may be altered with no record of their originals.
    /// </summary>
    [Fact]
    public async Task PartiallyReadableJournalRestoresWhatItCanAndSaysWhatItCannot()
    {
        var (rig, configurator) = Build();
        await configurator.ApplyAsync(Adapter(), Keyword, "4");
        rig.UnreadableLines = 1;

        var outcome = await configurator.RestoreAllAsync();

        Assert.Single(outcome.Restored);
        Assert.Equal(1, outcome.UnreadableRecords);
        Assert.True(outcome.NeedsAttention);
        Assert.False(rig.Discarded);
        Assert.Equal("0", rig.Values[Keyword]);
    }

    [Fact]
    public async Task RestoringAnEmptyJournalIsANoOp()
    {
        var (rig, configurator) = Build();

        var outcome = await configurator.RestoreAllAsync();

        Assert.True(outcome.NothingToDo);
        Assert.False(rig.Cleared);
    }

}
