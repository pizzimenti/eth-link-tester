using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EthLinkTester.Platform;

/// <summary>
/// Finding and loading the Rust engine, and saying what its result codes mean.
/// </summary>
/// <remarks>
/// <para>
/// Shared because <see cref="NativeLibrary.SetDllImportResolver"/> is registered per <i>assembly</i>
/// and may only be registered once. With the registration living in one type's static constructor,
/// every other <c>DllImport</c> in this assembly worked only if something had already touched that
/// type - which is a load-bearing dependency between two classes that have nothing to do with each
/// other, and it fails as a <c>DllNotFoundException</c> from a completely unrelated call.
/// </para>
/// <para>
/// The result codes are here for the same reason: two copies of a table mapping <c>-3</c> to a
/// sentence is exactly the duplicated knowledge that drifts once a code is added.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeEngineLibrary
{
    /// <summary>The cdylib's name, as every <c>DllImport</c> in this assembly spells it.</summary>
    public const string Name = "ethlink_engine";

    private static int _registered;

    /// <summary>
    /// Registers the resolver once, from wherever is first to need the engine.
    /// </summary>
    /// <remarks>
    /// Resolves the engine relative to this assembly rather than trusting the search path. "The
    /// application directory" is a different place under <c>dotnet run</c>, the packaged app and a
    /// test host, and the engine ships beside this assembly in all three.
    /// </remarks>
    public static void EnsureResolverRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(NativeEngineLibrary).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, Name, StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                // Npcap first: the engine imports wpcap.dll, and resolving that is what the loader
                // would otherwise fail at. Done here rather than in a static constructor so the
                // work happens when the engine is actually needed, and so merely referencing a type
                // from a test host does not touch the filesystem.
                //
                // The result is deliberately not fatal here. wpcap is delay-loaded, so the engine
                // resolves and answers elt_sample_size perfectly well without Npcap present, and
                // refusing the whole library would turn a missing prerequisite into an unloadable
                // assembly. What must not happen is a *pcap* call reaching Rust with no Npcap
                // behind it - the MSVC delay-load failure path raises a loader exception from
                // inside the engine, outside catch_unwind's contract, which can take the process
                // rather than returning a code. Every entry point that reaches pcap gates that.
                NpcapLoader.TryLoad();

                var beside = Path.Combine(
                    Path.GetDirectoryName(assembly.Location) ?? string.Empty, Name + ".dll");

                return File.Exists(beside) && NativeLibrary.TryLoad(beside, out var handle)
                    ? handle
                    : IntPtr.Zero;
            });
    }

    /// <summary>
    /// What a non-zero result code means, in words a user can act on.
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>ELT_ERR_*</c> constants in <c>engine/src/ffi.rs</c>. Codes an entry point
    /// cannot return are still listed: the unrecognised-code fallback exists for a genuinely new
    /// code, and it is a worse message than any of these.
    /// </remarks>
    public static string Describe(int code) => code switch
    {
        -1 => "The engine rejected a null argument.",
        -2 => "A device name was not valid UTF-8.",
        -3 =>
            "Npcap could not open one of the adapters. It is usually a device name that no longer " +
            "matches an adapter, or the process not running elevated.",
        -4 => "The engine faulted internally. The run was abandoned; adapter settings are unaffected.",
        -5 => "The engine is stopped, or another call is using it.",
        -6 => "Transmit and receive named the same adapter, so no frame would cross a cable.",
        -7 => "The frame size is outside what an Ethernet link can carry.",
        -8 => "The engine and this build disagree about a buffer length.",
        _ => $"The engine returned an unrecognised code ({code}).",
    };
}
