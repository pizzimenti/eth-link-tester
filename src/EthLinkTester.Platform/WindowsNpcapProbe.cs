using System.Globalization;
using System.ServiceProcess;
using EthLinkTester.Core.Preflight;
using Microsoft.Win32;

namespace EthLinkTester.Platform;

/// <summary>
/// Detects Npcap from the registry, the install directory, and the driver service.
/// </summary>
/// <remarks>
/// Three independent signals rather than one, because each can be present without the others: the
/// registry key survives an incomplete uninstall, the directory can exist with the service
/// stopped, and the service name is the only thing that proves the driver is actually loaded.
/// </remarks>
public sealed class WindowsNpcapProbe : INpcapProbe
{
    /// <summary>
    /// Npcap's installer is 32-bit, so its key lands under WOW6432Node even on 64-bit Windows.
    /// The native path is checked too rather than assuming that never changes.
    /// </summary>
    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\WOW6432Node\Npcap",
        @"SOFTWARE\Npcap",
    ];

    private const string ServiceName = "npcap";

    public Task<NpcapStatus> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var key = OpenNpcapKey();
                var installPath = key?.GetValue("") as string
                    ?? key?.GetValue("InstallPath") as string;

                // The driver directory is the signal that survives a registry key left behind by a
                // failed uninstall: no wpcap.dll means nothing can capture, whatever the registry
                // says.
                var driverDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
                var hasDriverFiles = File.Exists(Path.Combine(driverDirectory, "wpcap.dll"));

                var installed = hasDriverFiles || key is not null;
                if (!installed)
                {
                    return NpcapStatus.Absent;
                }

                return new NpcapStatus
                {
                    Installed = true,
                    Version = ReadVersion(key, driverDirectory),
                    ServiceRunning = IsServiceRunning(),
                    WinPcapCompatibilityMode = DetectWinPcapMode(key),
                    InstallPath = installPath ?? (hasDriverFiles ? driverDirectory : null),
                };
            },
            cancellationToken);

    private static RegistryKey? OpenNpcapKey()
    {
        foreach (var path in RegistryKeys)
        {
            var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is not null)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The installed version, preferring the DLL's own file version over the registry.
    /// </summary>
    /// <remarks>
    /// The registry value is written by the installer and can be stale after a repair or a manual
    /// file replacement; the DLL is the thing that actually gets loaded.
    /// </remarks>
    private static Version? ReadVersion(RegistryKey? key, string driverDirectory)
    {
        var wpcap = Path.Combine(driverDirectory, "wpcap.dll");
        if (File.Exists(wpcap))
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(wpcap);
            if (info.FileMajorPart > 0)
            {
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
        }

        var text = key?.GetValue("Version") as string;
        return Version.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Whether Npcap replaced the system-wide WinPcap libraries.
    /// </summary>
    /// <remarks>
    /// The registry flag is authoritative when present. The file check is the fallback and the
    /// reason it works: with compatibility mode off, wpcap.dll exists only under System32\Npcap,
    /// so finding one directly in System32 means something installed itself system-wide.
    /// </remarks>
    private static bool DetectWinPcapMode(RegistryKey? key)
    {
        if (key?.GetValue("WinPcapCompatible") is { } flag)
        {
            return Convert.ToInt32(flag, CultureInfo.InvariantCulture) != 0;
        }

        return File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "wpcap.dll"));
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            // No such service: Npcap's files are present but the driver was never registered.
            return false;
        }
    }
}
