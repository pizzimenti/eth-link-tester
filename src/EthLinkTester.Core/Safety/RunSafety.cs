using EthLinkTester.Core.Adapters;

namespace EthLinkTester.Core.Safety;

/// <summary>What is risky about a run, in the order a person should hear it.</summary>
public enum RunHazard
{
    /// <summary>The run will take the machine's network connection down.</summary>
    DefaultRoute,

    /// <summary>An adapter is disabled or unplugged, so the run cannot proceed as planned.</summary>
    AdapterNotUp,

    /// <summary>A previous run left changes behind that have not been put back.</summary>
    UnrestoredChanges,
}

/// <summary>
/// One thing the user should know before a run starts.
/// </summary>
/// <remarks>
/// Headline and detail are separate so the UI can lead with the consequence and let the
/// explanation follow. Both are plain text with no colour or icon implied: a warning that is only
/// distinguishable by colour is invisible to a meaningful share of users, and this app's verdicts
/// are always icon plus text.
/// </remarks>
public sealed record RunWarning
{
    public required RunHazard Hazard { get; init; }

    /// <summary>The consequence, in one line.</summary>
    public required string Headline { get; init; }

    /// <summary>Why it happens and what to expect.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Whether the user must actively agree before the run may start, rather than merely being
    /// told. Reserved for hazards that disrupt something outside the app.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    public override string ToString() => Headline;
}

/// <summary>
/// Inspects a proposed run and reports what the user should be told first.
/// </summary>
/// <remarks>
/// Deliberately advisory rather than prohibitive. The disruption policy for this app is that
/// <em>any</em> adapter may be tested, including the one carrying the default route - refusing
/// would make the tool useless on a single-NIC machine, and the honest alternative is to say
/// plainly what is about to happen and require an explicit yes.
/// </remarks>
public static class RunSafety
{
    public static IReadOnlyList<RunWarning> Inspect(
        IEnumerable<NetworkAdapterInfo> targets,
        IReadOnlyList<PendingRestore>? unrestored = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var warnings = new List<RunWarning>();

        foreach (var adapter in targets)
        {
            if (adapter.CarriesDefaultRoute)
            {
                warnings.Add(new RunWarning
                {
                    Hazard = RunHazard.DefaultRoute,
                    RequiresConfirmation = true,
                    Headline =
                        $"Testing {adapter.Name} will disconnect this machine from the network.",
                    Detail =
                        $"{adapter.Name} carries the default route, so it is how this machine " +
                        "reaches everything else. The run forces its speed and re-links it " +
                        "repeatedly, which drops internet access, remote sessions, and network " +
                        "drives for the duration. Settings are recorded before any change and put " +
                        "back when the run ends - including after a crash, on the next launch.",
                });
            }

            if (adapter.Status != AdapterStatus.Up)
            {
                warnings.Add(new RunWarning
                {
                    Hazard = RunHazard.AdapterNotUp,
                    Headline = $"{adapter.Name} is {adapter.Status.ToString().ToLowerInvariant()}.",
                    Detail = adapter.Status == AdapterStatus.Disabled
                        ? "The adapter is switched off in Windows and reports no counters at all. " +
                          "Enable it before running, or results will be empty rather than failing."
                        : "No link is present. That may be the fault under test, but it also means " +
                          "the adapter cannot demonstrate what it supports, so the test matrix is " +
                          "built from weaker evidence than usual.",
                });
            }
        }

        if (unrestored is { Count: > 0 })
        {
            warnings.Add(new RunWarning
            {
                Hazard = RunHazard.UnrestoredChanges,
                RequiresConfirmation = true,
                Headline = unrestored.Count == 1
                    ? "1 setting from a previous run has not been put back."
                    : $"{unrestored.Count} settings from a previous run have not been put back.",
                Detail =
                    "A previous run did not finish cleanly. Restore these before starting a new " +
                    "run, or the new run will record the leftover values as the originals and " +
                    "make them permanent: " +
                    string.Join("; ", unrestored.Select(e => e.ToString())),
            });
        }

        return warnings;
    }

    /// <summary>True when nothing may start until the user actively agrees.</summary>
    public static bool NeedsConfirmation(IEnumerable<RunWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        return warnings.Any(w => w.RequiresConfirmation);
    }
}
