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

    /// <summary>
    /// Npcap's own versions are 0.x and 1.x, so a major at or above this came from the
    /// WinPcap-compatibility version resource rather than from Npcap.
    /// </summary>
    private const int WinPcapCompatibilityMajor = 2;

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

                if (!hasDriverFiles && key is null)
                {
                    return NpcapStatus.Absent;
                }

                return new NpcapStatus
                {
                    Installed = true,
                    DriverFilesPresent = hasDriverFiles,
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
    /// The installed version, from the registry in preference to the DLL's file version.
    /// </summary>
    /// <remarks>
    /// The obvious ordering is wrong here. Npcap ships a <c>wpcap.dll</c> whose version resource
    /// deliberately advertises a <em>WinPcap</em> version - historically 4.1.0.2980 - so that
    /// applications expecting WinPcap bind to it happily. Reading that as Npcap's own version
    /// would report "Npcap 4.1.0 is ready" on every installation and silently disable the
    /// minimum-version check, since 4.1 clears any 1.x floor.
    /// <para>
    /// So the registry, which the installer writes with the real Npcap version, is authoritative.
    /// The file version is a fallback only, and a major version at or above 2 is rejected as the
    /// compatibility resource rather than believed - Npcap's own numbering is 0.x and 1.x.
    /// </para>
    /// </remarks>
    private static Version? ReadVersion(RegistryKey? key, string driverDirectory)
    {
        if (Version.TryParse(key?.GetValue("Version") as string, out var fromRegistry))
        {
            return fromRegistry;
        }

        var wpcap = Path.Combine(driverDirectory, "wpcap.dll");
        if (!File.Exists(wpcap))
        {
            return null;
        }

        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(wpcap);
        if (info.FileMajorPart is 0 or >= WinPcapCompatibilityMajor)
        {
            return null;
        }

        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
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
        // Tolerant of the value's type. Convert.ToInt32 throws FormatException on a string like
        // "yes" and InvalidCastException on REG_BINARY, and either escapes DetectAsync and
        // degrades the whole preflight to "could not determine" over one unexpected registry
        // value.
        switch (key?.GetValue("WinPcapCompatible"))
        {
            case int number:
                return number != 0;
            case string text when int.TryParse(text, CultureInfo.InvariantCulture, out var parsed):
                return parsed != 0;
            case string text:
                return text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
            case byte[] { Length: > 0 } bytes:
                return bytes[0] != 0;
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
