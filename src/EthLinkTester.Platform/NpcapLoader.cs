using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EthLinkTester.Platform;

/// <summary>
/// Makes Npcap's libraries findable before anything tries to load them.
/// </summary>
/// <remarks>
/// <para>
/// This is not optional and it is not defensive. Npcap installs <c>wpcap.dll</c> into
/// <c>System32\Npcap</c> rather than <c>System32</c> - precisely <em>because</em> WinPcap
/// API-compatible mode is off, that mode's whole function being to drop the libraries system-wide.
/// The directory is not on the default search path, so a process that does not add it fails to
/// load the engine at all. Observed exactly once, the hard way: exit code 53, no output, no
/// diagnostic.
/// </para>
/// <para>
/// <see cref="SetDefaultDllDirectories"/> is used rather than editing <c>PATH</c>. Prepending to
/// PATH would also work and would leave the process searching attacker-writable directories for
/// every subsequent load; restricting the search to the system directories plus this one
/// explicitly added path is both the working fix and the safe one, which is a rare combination
/// worth taking.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class NpcapLoader
{
    /// <summary>
    /// System32, the application directory, and anything added by <c>AddDllDirectory</c>.
    /// </summary>
    /// <remarks>
    /// The application directory has to be in this set. Restricting the search to System32 and
    /// user directories alone is the safer-sounding choice and it locks the process out of loading
    /// its own libraries - including the engine sitting beside the assembly. Discovered by doing
    /// exactly that: a DllNotFoundException for our own DLL, nothing to do with Npcap.
    /// </remarks>
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>The directory Npcap installs its libraries into.</summary>
    public static string NpcapDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");

    /// <summary>
    /// Adds Npcap's directory to this process's library search path. Safe to call repeatedly.
    /// </summary>
    /// <returns>False when the directory is absent, which means Npcap is not installed.</returns>
    public static bool EnsureSearchPath()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return true;
            }

            if (!Directory.Exists(NpcapDirectory))
            {
                return false;
            }

            SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);

            if (AddDllDirectory(NpcapDirectory) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Could not add '{NpcapDirectory}' to the library search path, so the engine " +
                    "cannot load Npcap.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            }

            _configured = true;
            return true;
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
