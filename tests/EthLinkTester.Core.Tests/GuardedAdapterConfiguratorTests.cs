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

        public bool Cleared { get; private set; }

        public Task<IReadOnlyList<AdapterProperty>> ReadPropertiesAsync(
            string adapterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdapterProperty>>(
            [
                new AdapterProperty
                {
                    Keyword = Keyword,
                    DisplayName = "Speed & Duplex",
                    RegistryValue = Values[Keyword],
                    DefaultRegistryValue = "0",
                    Options =
                    [
                        new() { RegistryValue = "0", DisplayValue = "Auto Negotiation" },
                        new() { RegistryValue = "1", DisplayValue = "10 Mbps Half Duplex" },
                        new() { RegistryValue = "2", DisplayValue = "10 Mbps Full Duplex" },
                        new() { RegistryValue = "3", DisplayValue = "100 Mbps Half Duplex" },
                        new() { RegistryValue = "4", DisplayValue = "100 Mbps Full Duplex" },
                    ],
                },
            ]);

        public Task WriteAsync(
            string adapterId, string keyword, string registryValue,
            CancellationToken cancellationToken = default)
        {
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
            Log.Add($"journal {entry.PropertyKeyword}={entry.OriginalValue}");
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingRestore>> ReadPendingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingRestore>>(Entries);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared = true;
            Entries.Clear();
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

    [Fact]
    public async Task RestoringAnEmptyJournalIsANoOp()
    {
        var (rig, configurator) = Build();

        var outcome = await configurator.RestoreAllAsync();

        Assert.True(outcome.NothingToDo);
        Assert.False(rig.Cleared);
    }

}
