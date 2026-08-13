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
        Assert.Empty(await journal.ReadPendingAsync());
    }

    [Fact]
    public async Task RecordsAnEntry()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());

        var pending = Assert.Single(await journal.ReadPendingAsync());
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
        var pending = Assert.Single(await afterRestart.ReadPendingAsync());
        Assert.Equal("1.0 Gbps Full Duplex", pending.OriginalValue);
    }

    [Fact]
    public async Task PreservesEntryOrder()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry("*SpeedDuplex", "Auto Negotiation"));
        await journal.RecordAsync(Entry("*FlowControl", "Rx & Tx Enabled"));
        await journal.RecordAsync(Entry("*JumboPacket", "Disabled"));

        var pending = await journal.ReadPendingAsync();

        Assert.Equal(
            ["*SpeedDuplex", "*FlowControl", "*JumboPacket"],
            pending.Select(p => p.PropertyKeyword));
    }

    [Fact]
    public async Task ClearRemovesEverything()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());
        await journal.ClearAsync();

        Assert.Empty(await journal.ReadPendingAsync());
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task ClearOnAnAbsentJournalIsHarmless()
    {
        using var journal = NewJournal();
        await journal.ClearAsync();
        Assert.Empty(await journal.ReadPendingAsync());
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

        var pending = await journal.ReadPendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal("*FlowControl", pending[^1].PropertyKeyword);
    }

    [Fact]
    public async Task IgnoresBlankLines()
    {
        using var journal = NewJournal();
        await journal.RecordAsync(Entry());
        await File.AppendAllTextAsync(JournalPath, "\n\n   \n");

        Assert.Single(await journal.ReadPendingAsync());
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

        var pending = await journal.ReadPendingAsync();

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

    [Fact]
    public async Task ThrowsAfterDisposal()
    {
        var journal = NewJournal();
        journal.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.RecordAsync(Entry()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journal.ReadPendingAsync());
    }
}
