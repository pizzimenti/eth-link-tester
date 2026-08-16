using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EthLinkTester.Platform;

/// <summary>
/// Loads Npcap's libraries by absolute path, so the engine can bind to them.
/// </summary>
/// <remarks>
/// <para>
/// This is not optional and it is not defensive. Npcap installs <c>wpcap.dll</c> into
/// <c>System32\Npcap</c> rather than <c>System32</c> - precisely <em>because</em> WinPcap
/// API-compatible mode is off, that mode's whole function being to drop the libraries system-wide.
/// The directory is not on the default search path, so a process that does nothing about it fails
/// to load the engine at all. Observed exactly once, the hard way: exit code 53, no output, no
/// diagnostic.
/// </para>
/// <para>
/// <b>Pre-loading rather than changing the search path.</b> The first working version called
/// <c>SetDefaultDllDirectories</c> and <c>AddDllDirectory</c>, which fixed it by rewriting where
/// <em>every</em> subsequent load in the process looks - a change with no owner, no scope, and no
/// way to undo, made from a static constructor that ran whenever the type was first touched.
/// It also broke once already in the obvious way: the flag combination that sounded safest
/// excluded the application directory and locked the process out of loading its own engine.
/// Loading the two libraries by full path leaves the search path alone; once a module named
/// <c>wpcap.dll</c> is in the process, the loader satisfies the engine's import from it without
/// searching anywhere.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class NpcapLoader
{
    /// <summary>
    /// Packet.dll first: wpcap.dll depends on it, and loading it explicitly means the dependency
    /// is resolved from a known path rather than by whatever the search order turns up.
    /// </summary>
    private static readonly string[] Libraries = ["Packet.dll", "wpcap.dll"];

    private static readonly Lock Gate = new();
    private static bool _loaded;

    /// <summary>The directory Npcap installs its libraries into.</summary>
    public static string NpcapDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");

    /// <summary>
    /// Loads Npcap's libraries into this process. Safe to call repeatedly.
    /// </summary>
    /// <returns>
    /// False when Npcap is not installed, which is an expected state: the app still runs, and the
    /// preflight check is what tells the user about it. A library that is present but will not
    /// load is a broken installation and throws, because silently reporting "not installed" would
    /// send the user to reinstall something they already have.
    /// </returns>
    public static bool TryLoad()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return true;
            }

            foreach (var library in Libraries)
            {
                var path = Path.Combine(NpcapDirectory, library);

                if (!File.Exists(path))
                {
                    return false;
                }

                // The handle is deliberately not kept. Windows reference-counts loaded modules and
                // nothing here ever wants to unload one - the engine binds to these for the life
                // of the process.
                if (!NativeLibrary.TryLoad(path, out _))
                {
                    throw new InvalidOperationException(
                        $"'{path}' exists but could not be loaded, so the engine cannot use Npcap. " +
                        "The usual cause is a 32-bit Npcap installation under a 64-bit process.",
                        new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }

            _loaded = true;
            return true;
        }
    }
}
