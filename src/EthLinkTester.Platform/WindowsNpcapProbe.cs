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
    /// The installed Npcap version, read from Npcap's own components rather than from
    /// <c>wpcap.dll</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never <c>wpcap.dll</c>.</b> That file is the libpcap-compatible layer and its version
    /// resource advertises <em>libpcap's</em> version, which is what applications expecting
    /// libpcap are meant to see. Measured against a real Npcap 1.88 install: <c>wpcap.dll</c>
    /// reports 1.10.6 while the product is 1.88. Reading it produced the headline "Npcap 1.10.6
    /// is ready" - a version that does not exist - and evaluated the minimum-version gate against
    /// a number belonging to a different project.
    /// </para>
    /// <para>
    /// <c>Packet.dll</c> sits in the same directory, is Npcap's own API component, and reports
    /// 1.88 correctly. The driver binary agrees, and the uninstall entry's DisplayVersion is the
    /// last resort. The registry key Npcap creates carries the install path, AdminOnly and
    /// WinPcapCompatible - but on 1.88 it carries no version at all, which is why a
    /// registry-first lookup silently fell through to the wrong file.
    /// </para>
    /// </remarks>
    private static Version? ReadVersion(RegistryKey? key, string driverDirectory)
    {
        if (Version.TryParse(key?.GetValue("Version") as string, out var fromRegistry))
        {
            return fromRegistry;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(driverDirectory, "Packet.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Drivers", "npcap.sys"),
        })
        {
            if (ReadFileVersion(candidate) is { } version)
            {
                return version;
            }
        }

        return Version.TryParse(ReadUninstallDisplayVersion(), out var fromUninstall)
            ? fromUninstall
            : null;
    }

    /// <summary>
    /// A binary's version, taken from the resource's <em>strings</em> rather than its numeric
    /// fields.
    /// </summary>
    /// <remarks>
    /// The two disagree, and only the strings are right. Measured on Npcap 1.88: both
    /// <c>Packet.dll</c> and <c>npcap.sys</c> report <c>FileVersion</c> and
    /// <c>ProductVersion</c> of "1.88" while their numeric
    /// <c>FileMajorPart.FileMinorPart.FileBuildPart</c> reads 5.1.88 - a WinPcap-era
    /// compatibility numbering carried in the same resource. The numeric fields are the more
    /// natural-looking API and produce a confident, plausible, wrong answer.
    /// </remarks>
    private static Version? ReadFileVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);

        foreach (var text in new[] { info.FileVersion, info.ProductVersion })
        {
            // Trims any build suffix a vendor appends after the numbers.
            if (Version.TryParse(text?.Split(' ')[0], out var parsed) && parsed != new Version(0, 0))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadUninstallDisplayVersion()
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst",
        })
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("DisplayVersion") is string version)
            {
                return version;
            }
        }

        return null;
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
            case long number:
                return number != 0;
            case byte[] { Length: > 0 } bytes:
                return bytes[0] != 0;
            // REG_MULTI_SZ and anything else numeric-ish. Falling through to the file heuristic
            // instead would report "not in compatibility mode" for a machine that is, which is
            // the dangerous direction for a state this app calls unusable.
            case IConvertible convertible:
                try
                {
                    return convertible.ToInt64(CultureInfo.InvariantCulture) != 0;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    break;
                }
            case string[] { Length: > 0 } lines:
                return lines[0] is not ("0" or "");
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No such service, or the service database refused the query. Either way the driver
            // cannot be shown to be running, and a preflight that throws tells the user nothing.
            return false;
        }
    }
}
