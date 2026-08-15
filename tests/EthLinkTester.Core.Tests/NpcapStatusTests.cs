using EthLinkTester.Core.Preflight;

namespace EthLinkTester.Core.Tests;

public class NpcapStatusTests
{
    private static NpcapStatus Installed(
        string version = "1.79",
        bool running = true,
        bool winPcapMode = false,
        bool driverFiles = true) => new()
        {
            Installed = true,
            DriverFilesPresent = driverFiles,
            Version = Version.Parse(version),
            ServiceRunning = running,
            WinPcapCompatibilityMode = winPcapMode,
        };

    /// <summary>
    /// Every form Npcap has actually shipped a version in. The suffixed ones are the point:
    /// Version.TryParse refuses them outright, and a version this cannot read becomes "unknown",
    /// which Readiness treats as Ready - silently disabling the minimum-version gate for exactly
    /// the ancient builds it exists to catch.
    /// </summary>
    [Theory]
    [InlineData("1.88", "1.88")]
    [InlineData("0.99-r9", "0.99")]
    [InlineData("0.9985", "0.9985")]
    [InlineData("1.10.6", "1.10.6")]
    [InlineData("1.79 build 2024", "1.79")]
    [InlineData("5", "5.0")]
    public void ParsesEveryVersionFormNpcapHasShipped(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), NpcapStatus.ParseVersion(text));

    /// <summary>
    /// Unparseable must stay null rather than becoming a zero that would read as a real version
    /// and fail the minimum check for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("r9")]
    [InlineData("0.0")]
    public void RefusesWhatItCannotRead(string? text) =>
        Assert.Null(NpcapStatus.ParseVersion(text));

    /// <summary>
    /// The regression that matters: an old build must still be caught as too old after parsing,
    /// not waved through because its version string had a suffix.
    /// </summary>
    [Fact]
    public void ASuffixedAncientVersionIsStillTooOld()
    {
        var status = new NpcapStatus
        {
            Installed = true,
            DriverFilesPresent = true,
            ServiceRunning = true,
            Version = NpcapStatus.ParseVersion("0.99-r9"),
        };

        Assert.Equal(NpcapReadiness.TooOld, status.Readiness);
    }

    [Fact]
    public void AWorkingInstallationIsReady()
    {
        var status = Installed();

        Assert.Equal(NpcapReadiness.Ready, status.Readiness);
        Assert.True(status.CanRunLiveTests);
        Assert.Null(status.Remedy);
    }

    /// <summary>
    /// A missing driver is not a fatal error. Simulation mode exists so the UI is developable
    /// with no capture driver and no second NIC, so the message must not read as a dead end.
    /// </summary>
    [Fact]
    public void AbsentIsReportedWithoutTreatingItAsFatal()
    {
        Assert.Equal(NpcapReadiness.NotInstalled, NpcapStatus.Absent.Readiness);
        Assert.False(NpcapStatus.Absent.CanRunLiveTests);
        Assert.Contains("simulation mode still works", NpcapStatus.Absent.Headline, StringComparison.Ordinal);
        Assert.Contains(NpcapStatus.DownloadUrl, NpcapStatus.Absent.Remedy!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The licence is the reason this is a prerequisite rather than a bundled component, and the
    /// user deserves to know why they are being sent to a download page.
    /// </summary>
    [Fact]
    public void ExplainsWhyNpcapIsNotBundled()
    {
        Assert.Contains("redistribution", NpcapStatus.Absent.Remedy!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstalledButStoppedIsDistinctFromAbsent()
    {
        var status = Installed(running: false);

        Assert.Equal(NpcapReadiness.ServiceStopped, status.Readiness);
        Assert.False(status.CanRunLiveTests);
        Assert.Contains("reboot", status.Remedy!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOlderVersionThanVerifiedIsCalledOut()
    {
        var status = Installed("0.99");

        Assert.Equal(NpcapReadiness.TooOld, status.Readiness);
        Assert.False(status.CanRunLiveTests);
    }

    [Fact]
    public void TheMinimumVersionItselfIsAccepted()
    {
        Assert.Equal(NpcapReadiness.Ready, Installed("1.0").Readiness);
    }

    /// <summary>
    /// WinPcap compatibility mode outranks a stopped service in the report: reinstalling to fix it
    /// restarts the service anyway, so leading with the service would send the user round twice.
    /// </summary>
    [Fact]
    public void WinPcapCompatibilityModeIsReportedAheadOfLesserProblems()
    {
        var status = Installed(running: false, winPcapMode: true);

        Assert.Equal(NpcapReadiness.WinPcapCompatibilityMode, status.Readiness);
        Assert.Contains("unchecked", status.Remedy!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown version must not be treated as too old. Reporting a working installation as
    /// unusable is worse than saying the version could not be read.
    /// </summary>
    [Fact]
    public void AnUnreadableVersionDoesNotFailThePreflight()
    {
        var status = new NpcapStatus
        {
            Installed = true,
            DriverFilesPresent = true,
            ServiceRunning = true,
        };

        Assert.Equal(NpcapReadiness.Ready, status.Readiness);
        Assert.Contains("version unknown", status.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry key proves registration and nothing else - it routinely survives an incomplete
    /// uninstall. Without the driver files nothing can capture, and "start the service" is the
    /// wrong advice for a machine that needs a reinstall.
    /// </summary>
    [Fact]
    public void ARegistryKeyWithoutDriverFilesIsABrokenInstallNotAStoppedService()
    {
        var status = Installed(driverFiles: false, running: false);

        Assert.Equal(NpcapReadiness.FilesMissing, status.Readiness);
        Assert.False(status.CanRunLiveTests);
        Assert.Contains("Reinstall", status.Remedy!, StringComparison.Ordinal);
        Assert.DoesNotContain("start the service", status.Remedy!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remedies are ordered by which subsumes the others: a reinstall fixes missing files,
    /// compatibility mode, and an old version, and restarts the service on the way.
    /// </summary>
    [Fact]
    public void MissingFilesOutrankEveryOtherProblem()
    {
        var status = Installed(version: "0.99", driverFiles: false, running: false, winPcapMode: true);

        Assert.Equal(NpcapReadiness.FilesMissing, status.Readiness);
    }
}
