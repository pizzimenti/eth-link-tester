using System;
using System.Threading;

namespace EthLinkTester.App;

/// <summary>
/// A process-wide claim on the rig's adapters, so two features cannot drive them at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two correct components with no coordination between them.</b> Lab Mode's page is cached
/// deliberately - <c>NavigationCacheMode.Required</c>, so a run survives the user visiting another
/// section - and the Rig page's topology detection forces <c>*SpeedDuplex</c> on the same NICs when
/// the disruptive probe is allowed. Nothing stopped someone starting a run, navigating to Rig, and
/// bouncing the link out from under a measurement that was still going: the engine keeps sampling
/// through the outage, and the numbers it publishes describe a link that went down mid-experiment.
/// </para>
/// <para>
/// A static gate rather than a service passed around, because the thing being protected really is
/// process-wide: there is one set of adapters and one cable, however many view models exist. The
/// two page view models are constructed independently by their pages, so there is no composition
/// root to hand a shared instance through - and inventing one to carry a single boolean would be
/// more machinery than the problem.
/// </para>
/// <para>
/// Claims are advisory in the sense that only code which asks is stopped, and that is enough:
/// there are exactly two things in this application that touch the adapters, and both ask.
/// </para>
/// </remarks>
internal static class HardwareSession
{
    /// <summary>
    /// The current holder, or null. One reference, so the state is always self-consistent.
    /// </summary>
    /// <remarks>
    /// A flag plus a separate holder string published them in two steps, and a reader landing
    /// between the two saw <c>IsBusy</c> true with <c>Holder</c> null - which the topology panel
    /// turns into a refusal with no message, the least useful thing a refusal can be. One
    /// atomically exchanged reference cannot be observed half-written.
    /// </remarks>
    private static string? _holder;

    /// <summary>Raised when a claim is taken or released, so commands can re-evaluate.</summary>
    /// <remarks>
    /// Handlers run on whichever thread released the claim. Both current subscribers are view
    /// models updating a command's executability, which the MVVM toolkit marshals for them.
    /// </remarks>
    public static event EventHandler? Changed;

    /// <summary>True while something is driving the adapters.</summary>
    public static bool IsBusy => Holder is not null;

    /// <summary>What holds the claim, in words a user can be shown.</summary>
    public static string? Holder => Volatile.Read(ref _holder);

    /// <summary>
    /// Takes the claim, or returns null when something else already holds it.
    /// </summary>
    /// <param name="owner">
    /// What to tell the user is using the hardware, e.g. "a Lab Mode run". Named rather than
    /// numbered because "the adapters are busy" is not an actionable thing to read.
    /// </param>
    public static IDisposable? TryClaim(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (Interlocked.CompareExchange(ref _holder, owner, null) is not null)
        {
            return null;
        }

        // After the state is published, never before: a handler that re-evaluates a command must
        // see the claim it is being told about.
        Changed?.Invoke(null, EventArgs.Empty);

        return new Claim();
    }

    private sealed class Claim : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            // Idempotent, because the natural way to hold one of these is a using block inside a
            // try/finally that may also release on a failure path.
            if (_released)
            {
                return;
            }

            _released = true;
            Volatile.Write(ref _holder, null);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
