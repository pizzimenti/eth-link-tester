using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Safety;

namespace EthLinkTester.Core.Tests;

public class RunSafetyTests
{
    private static NetworkAdapterInfo Adapter(
        string name = "Ethernet",
        AdapterStatus status = AdapterStatus.Up,
        bool defaultRoute = false) => new()
    {
        Id = name,
        Name = name,
        Description = "test adapter",
        MacAddress = "00-00-00-00-00-00",
        Status = status,
        CarriesDefaultRoute = defaultRoute,
    };

    private static PendingRestore Leftover(string keyword = "*SpeedDuplex") => new()
    {
        AdapterId = "Ethernet",
        AdapterName = "Ethernet",
        PropertyKeyword = keyword,
        OriginalValue = "0",
        RecordedUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void AHealthyRigRaisesNothing()
    {
        Assert.Empty(RunSafety.Inspect([Adapter(), Adapter("Ethernet 2")]));
    }

    /// <summary>
    /// The policy is that any adapter may be tested, including the one carrying the default
    /// route - refusing would make the tool useless on a single-NIC machine. The obligation that
    /// comes with that is saying plainly what is about to happen and requiring an explicit yes.
    /// </summary>
    [Fact]
    public void TestingTheDefaultRouteAdapterRequiresConfirmation()
    {
        var warnings = RunSafety.Inspect([Adapter("Ethernet", defaultRoute: true)]);

        var warning = Assert.Single(warnings);
        Assert.Equal(RunHazard.DefaultRoute, warning.Hazard);
        Assert.True(warning.RequiresConfirmation);
        Assert.True(RunSafety.NeedsConfirmation(warnings));

        // The consequence must be in the headline, not buried in the detail.
        Assert.Contains("disconnect", warning.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ethernet", warning.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Disabled and disconnected are different faults with different remedies, so they must not
    /// share a message: one is a switch to flip, the other may be the thing under test.
    /// </summary>
    [Theory]
    [InlineData(AdapterStatus.Disabled, "Enable it")]
    [InlineData(AdapterStatus.Disconnected, "No link")]
    public void AnAdapterThatIsNotUpIsCalledOutSpecifically(AdapterStatus status, string expected)
    {
        var warning = Assert.Single(RunSafety.Inspect([Adapter("Ethernet", status)]));

        Assert.Equal(RunHazard.AdapterNotUp, warning.Hazard);
        Assert.Contains(expected, warning.Detail, StringComparison.Ordinal);

        // Not up is worth saying, but it does not disrupt anything outside the app.
        Assert.False(warning.RequiresConfirmation);
    }

    /// <summary>
    /// The nastiest ordering hazard in the app: starting a new run over leftovers makes the
    /// leftover values look like the originals, so restoring afterwards makes them permanent.
    /// </summary>
    [Fact]
    public void LeftoverChangesFromAPreviousRunRequireConfirmation()
    {
        var warnings = RunSafety.Inspect([Adapter()], [Leftover(), Leftover("*FlowControl")]);

        var warning = Assert.Single(warnings);
        Assert.Equal(RunHazard.UnrestoredChanges, warning.Hazard);
        Assert.True(warning.RequiresConfirmation);
        Assert.Contains("2 settings", warning.Headline, StringComparison.Ordinal);
        Assert.Contains("*SpeedDuplex", warning.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleLeftoverReadsAsSingular()
    {
        var warning = Assert.Single(RunSafety.Inspect([Adapter()], [Leftover()]));

        Assert.Contains("1 setting from", warning.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningsAreRaisedPerAdapterNotJustForTheFirst()
    {
        var warnings = RunSafety.Inspect(
        [
            Adapter("Ethernet", defaultRoute: true),
            Adapter("Ethernet 2", AdapterStatus.Disabled),
        ]);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Hazard == RunHazard.DefaultRoute);
        Assert.Contains(warnings, w => w.Hazard == RunHazard.AdapterNotUp);
    }
}
