using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Tests;

public sealed class FileRestoreJournalTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ethlink-journal-tests", Guid.NewGuid().ToString("n"));

    private string JournalPath => Path.Combine(_directory, "pending-restore.jsonl");

    private FileRestoreJournal NewJournal() => new(JournalPath);

    private static PendingRestore Entry(
        string keyword = "*SpeedDuplex",
        string original = "Auto Negotiation",
        string adapter = "Ethernet") => new()
        {
            AdapterId = "{503593B4-FF35-43DB-BE33-B8D9AF979B28}",
            AdapterName = adapter,
            PropertyKeyword = keyword,
            PropertyDisplayName = "Speed & Duplex",
            OriginalValue = original,
            RecordedUtc = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
        };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task NoJournalMeansNothingPending()
    {
        using var journal = NewJournal();
        Assert.Empty((await journal.ReadPendingAsync()).Entries);
    }

    [Fact]
    public async Task RecordsAnEntry()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());

        var pending = Assert.Single((await journal.ReadPendingAsync()).Entries);
        Assert.Equal("*SpeedDuplex", pending.PropertyKeyword);
        Assert.Equal("Auto Negotiation", pending.OriginalValue);
        Assert.Equal("Ethernet", pending.AdapterName);
    }

    /// <summary>
    /// The whole point: a fresh instance with no shared state - which is what a relaunch after a
    /// crash looks like - must see the entry.
    /// </summary>
    [Fact]
    public async Task EntrySurvivesProcessDeath()
    {
        using (var writer = NewJournal())
        {
            await writer.RecordAsync(Entry(original: "1.0 Gbps Full Duplex"));
        }

        using var afterRestart = NewJournal();
        var pending = Assert.Single((await afterRestart.ReadPendingAsync()).Entries);
        Assert.Equal("1.0 Gbps Full Duplex", pending.OriginalValue);
    }

    [Fact]
    public async Task PreservesEntryOrder()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry("*SpeedDuplex", "Auto Negotiation"));
        await journal.RecordAsync(Entry("*FlowControl", "Rx & Tx Enabled"));
        await journal.RecordAsync(Entry("*JumboPacket", "Disabled"));

        var pending = (await journal.ReadPendingAsync()).Entries;

        Assert.Equal(
            ["*SpeedDuplex", "*FlowControl", "*JumboPacket"],
            pending.Select(p => p.PropertyKeyword));
    }

    [Fact]
    public async Task DiscardRemovesEverything()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());
        await journal.DiscardAsync();

        Assert.Empty((await journal.ReadPendingAsync()).Entries);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task DiscardOnAnAbsentJournalIsHarmless()
    {
        using var journal = NewJournal();
        await journal.DiscardAsync();
        Assert.Empty((await journal.ReadPendingAsync()).Entries);
    }

    /// <summary>
    /// A crash partway through an append leaves a truncated final line. Every intact entry
    /// before it must still be recoverable - one bad record cannot be allowed to strand the
    /// rest, because those are the values that put the user's network back.
    /// </summary>
    [Fact]
    public async Task TruncatedFinalLineDoesNotStrandEarlierEntries()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry("*SpeedDuplex", "Auto Negotiation"));
        await journal.RecordAsync(Entry("*FlowControl", "Rx & Tx Enabled"));

        // Simulate dying mid-write.
        await File.AppendAllTextAsync(JournalPath, "{\"AdapterId\":\"{503593B4\",\"Adapt");

        var contents = await journal.ReadPendingAsync();

        Assert.Equal(2, contents.Entries.Count);
        Assert.Equal("*FlowControl", contents.Entries[^1].PropertyKeyword);

        // Classified as a torn tail, not corruption: the record was journaled before its adapter
        // change was applied, so an incomplete one describes a change that never happened.
        Assert.True(contents.HasTornFinalLine);
        Assert.Equal(0, contents.UnreadableLines);
    }

    [Fact]
    public async Task IgnoresBlankLines()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());
        await File.AppendAllTextAsync(JournalPath, "\n\n   \n");

        Assert.Single((await journal.ReadPendingAsync()).Entries);
    }

    /// <summary>
    /// Concurrent records must not interleave into corrupt lines. A run snapshots several
    /// properties across two adapters, and losing one to a torn write loses a setting the user
    /// never gets back.
    /// </summary>
    [Fact]
    public async Task ConcurrentRecordsAreAllDurableAndIntact()
    {
        using var journal = NewJournal();

        await Task.WhenAll(
            Enumerable.Range(0, 50)
                      .Select(i => journal.RecordAsync(Entry($"*Property{i}", $"value{i}"))));

        var pending = (await journal.ReadPendingAsync()).Entries;

        Assert.Equal(50, pending.Count);
        Assert.Equal(50, pending.Select(p => p.PropertyKeyword).Distinct().Count());
    }

    [Fact]
    public async Task CreatesTheDirectoryIfItDoesNotExist()
    {
        Assert.False(Directory.Exists(_directory));

        using var journal = NewJournal();
        await journal.RecordAsync(Entry());

        Assert.True(File.Exists(JournalPath));
    }

    /// <summary>
    /// The worst defect found in review, and the reason a truncated line is not self-limiting:
    /// appending after an unterminated fragment glues the new record onto it, so one torn write
    /// made every subsequent entry unreadable too. The NIC stayed forced at 100 Mbps with a
    /// journal that could no longer describe how to put it back.
    /// </summary>
    [Fact]
    public async Task AnEntryWrittenAfterATornWriteIsStillReadable()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry("*SpeedDuplex", "Auto Negotiation"));

        // Dying mid-append leaves a record with no terminator.
        await File.AppendAllTextAsync(JournalPath, "{\"AdapterId\":\"{503593B4\",\"Adapt");

        await journal.RecordAsync(Entry("*FlowControl", "Rx & Tx Enabled"));

        var contents = await journal.ReadPendingAsync();

        Assert.Equal(2, contents.Entries.Count);
        Assert.Equal("*FlowControl", contents.Entries[^1].PropertyKeyword);
        Assert.Equal(0, contents.UnreadableLines);
    }

    /// <summary>
    /// A journal that is present but entirely unreadable is not an empty one. Reporting it as
    /// empty left it on disk forever - poisoning every later append - while telling the user
    /// nothing was wrong, with adapters still forced.
    /// </summary>
    [Fact]
    public async Task AnEntirelyCorruptJournalIsDistinguishableFromAnEmptyOne()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(JournalPath, "not json at all\nnor is this\n");

        using var journal = NewJournal();
        var contents = await journal.ReadPendingAsync();

        Assert.True(contents.IsUnreadable);
        Assert.False(contents.IsEmpty);
        Assert.Equal(2, contents.UnreadableLines);
    }

    [Fact]
    public async Task AnAbsentJournalIsEmptyRatherThanUnreadable()
    {
        using var journal = NewJournal();
        var contents = await journal.ReadPendingAsync();

        Assert.True(contents.IsEmpty);
        Assert.False(contents.IsUnreadable);
    }

    /// <summary>
    /// Regression, caught by mutation: the earlier test only ever put the corrupt line last, so
    /// stopping at the first bad line passed anyway. A torn write in the middle must not strand
    /// the entries after it.
    /// </summary>
    [Fact]
    public async Task ACorruptLineInTheMiddleDoesNotStrandLaterEntries()
    {
        Directory.CreateDirectory(_directory);
        using var journal = NewJournal();

        await journal.RecordAsync(Entry("*SpeedDuplex", "Auto Negotiation"));
        await File.AppendAllTextAsync(JournalPath, "{ this line is broken }\n");
        await journal.RecordAsync(Entry("*JumboPacket", "Disabled"));

        var contents = await journal.ReadPendingAsync();

        Assert.Equal(2, contents.Entries.Count);
        Assert.Equal(1, contents.UnreadableLines);
        Assert.Equal("*JumboPacket", contents.Entries[^1].PropertyKeyword);
    }

    /// <summary>
    /// The TOCTOU defect: a restore pass takes seconds, and anything recorded during it describes
    /// an adapter that has already been changed. Removing by identity keeps those; deleting the
    /// whole file destroyed them.
    /// </summary>
    [Fact]
    public async Task RemovingRestoredEntriesKeepsOnesRecordedSince()
    {
        using var journal = NewJournal();
        var first = Entry("*SpeedDuplex", "Auto Negotiation");
        await journal.RecordAsync(first);

        // Recorded after the restore pass read the journal.
        var during = Entry("*FlowControl", "Rx & Tx Enabled");
        await journal.RecordAsync(during);

        await journal.RemoveAsync([first]);

        var remaining = (await journal.ReadPendingAsync()).Entries;

        Assert.Single(remaining);
        Assert.Equal("*FlowControl", remaining[0].PropertyKeyword);
    }

    [Fact]
    public async Task RemovingEveryEntryDeletesTheFile()
    {
        using var journal = NewJournal();
        var entry = Entry();
        await journal.RecordAsync(entry);

        await journal.RemoveAsync([entry]);

        Assert.False(File.Exists(JournalPath));
    }

    /// <summary>
    /// A second instance recovering at startup must not corrupt or lock out a running one. With
    /// per-instance locking only, 23 of 40 concurrent records threw sharing violations.
    /// </summary>
    [Fact]
    public async Task TwoJournalInstancesCanWriteTheSameFile()
    {
        using var first = NewJournal();
        using var second = NewJournal();

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => first.RecordAsync(Entry($"*First{i}", $"a{i}")))
                .Concat(
            Enumerable.Range(0, 20).Select(i => second.RecordAsync(Entry($"*Second{i}", $"b{i}")))));

        var contents = await first.ReadPendingAsync();

        Assert.Equal(40, contents.Entries.Count);
        Assert.Equal(0, contents.UnreadableLines);
    }

    [Fact]
    public async Task ThrowsAfterDisposal()
    {
        var journal = NewJournal();
        journal.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.RecordAsync(Entry()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.ReadPendingAsync());
    }
}
