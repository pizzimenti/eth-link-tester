using EthLinkTester.Core.Preflight;

namespace EthLinkTester.Core.Tests;

public class NpcapStatusTests
{
    private static NpcapStatus Installed(
        string version = "1.79",
        bool running = true,
        bool winPcapMode = false) => new()
        {
            Installed = true,
            Version = Version.Parse(version),
            ServiceRunning = running,
            WinPcapCompatibilityMode = winPcapMode,
        };

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
        var status = new NpcapStatus { Installed = true, ServiceRunning = true };

        Assert.Equal(NpcapReadiness.Ready, status.Readiness);
        Assert.Contains("version unknown", status.Headline, StringComparison.Ordinal);
    }
}
