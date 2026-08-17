namespace EthLinkTester.Core.Topology;

/// <summary>What the signals, taken together, say the link is.</summary>
public enum TopologyConclusion
{
    /// <summary>
    /// Not established. The honest answer whenever nothing positive was observed, and far more
    /// common than it looks - most of these signals cannot prove a direct connection at all.
    /// </summary>
    Unknown,

    /// <summary>The two NICs are wired to each other.</summary>
    Direct,

    /// <summary>Something is bridging between them.</summary>
    Bridged,
}

/// <summary>How much the conclusion is worth.</summary>
public enum TopologyConfidence
{
    /// <summary>Nothing was established.</summary>
    None,

    /// <summary>One suggestive signal, or several that only just agree.</summary>
    Low,

    /// <summary>A strong signal, or several agreeing suggestive ones.</summary>
    Moderate,

    /// <summary>A conclusive signal. The alternative would be physically impossible.</summary>
    High,
}

/// <summary>
/// The combined topology answer, with the evidence that produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately asymmetric, and the asymmetry is the whole design.</b> The two wrong
/// answers do not cost the same. Calling a direct cable "bridged" wastes someone's afternoon.
/// Calling a bridged path "direct" makes every later result a lie: the run then measures two
/// cables and a switch, and the report attributes all of it to one cable, with a grade and a
/// confidence interval attached. Phase 6 grades against IEEE limits on that basis. A quiet wrong
/// answer here contaminates everything downstream and nothing later can detect it.
/// </para>
/// <para>
/// So <see cref="TopologyConclusion.Bridged"/> is reached readily and
/// <see cref="TopologyConclusion.Direct"/> is not. Every signal here can prove a bridge; almost
/// none can prove a cable. Absence of evidence never becomes evidence of absence: seeing no LLDP,
/// or a latency slope that looks single-hop, leaves the question open rather than answering it.
/// </para>
/// </remarks>
/// <param name="Conclusion">What the link is, or that it is not established.</param>
/// <param name="Confidence">How much that is worth.</param>
/// <param name="Observations">Every signal that ran, including the ones that settled nothing.</param>
public sealed record TopologyVerdict(
    TopologyConclusion Conclusion,
    TopologyConfidence Confidence,
    IReadOnlyList<TopologyObservation> Observations)
{
    /// <summary>
    /// Which pair of adapters this describes, and when it was taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A verdict with no identity cannot be joined to anything, and one with no timestamp cannot be
    /// re-checked. Both become primary-key material the moment Phase 6 persists these, which is why
    /// they are here before that happens rather than as a schema migration afterwards.
    /// </para>
    /// <para>
    /// The timestamp is load-bearing for a reason that is easy to miss: a verdict taken before a
    /// suite is not evidence about the path during it. An RFC 2544 latency run alone is 120 seconds
    /// per frame size repeated twenty times, and over hours a link can flap, a driver can reload -
    /// this rig's USB adapter already does under small-frame load - or a switch can be inserted
    /// between phases. Re-running the free signals at each phase boundary and comparing against the
    /// opening verdict needs the opening verdict to know when it was.
    /// </para>
    /// </remarks>
    public string? TransmitAdapterId { get; init; }

    /// <inheritdoc cref="TransmitAdapterId"/>
    public string? ReceiveAdapterId { get; init; }

    /// <inheritdoc cref="TransmitAdapterId"/>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>
    /// Whether a cable grade derived from this run can honestly be attributed to the cable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a confidently direct link earns this. Unknown does not, which is the point: a report
    /// that cannot say what it measured must not grade a cable, and the default answer is Unknown.
    /// Suite Mode should offer to re-run direct rather than grade anyway.
    /// </para>
    /// <para>
    /// <b>The gate stays at Moderate; the work went into what may reach it.</b> The alternative was
    /// to raise the bar to High, and that would have been the wrong repair: High is reserved for a
    /// signal whose alternative is physically impossible, and every such signal in this set argues
    /// for a <i>bridge</i>. Requiring it would make Direct unreachable and grading dead code. So the
    /// two signals that can produce Direct were hardened instead - the multicast sweep demoted to
    /// corroborating, and the forced-speed probe required to witness an actual transition - and
    /// Moderate now means "one alias-hardened Strong signal, or two independent suggestive ones",
    /// which is what the confidence documentation claimed all along.
    /// </para>
    /// </remarks>
    public bool GradingIsAttributable =>
        Conclusion == TopologyConclusion.Direct && Confidence >= TopologyConfidence.Moderate;

    /// <summary>One sentence a report can print, naming the conclusion and its basis.</summary>
    public string Summary
    {
        get
        {
            var basis = Observations.FirstOrDefault(o => o.Finding != TopologyFinding.Inconclusive);

            return Conclusion switch
            {
                TopologyConclusion.Bridged when basis is not null =>
                    $"A switch or bridge is in the path ({Confidence.ToString().ToLowerInvariant()} "
                    + $"confidence): {basis.Detail}",
                TopologyConclusion.Direct when basis is not null =>
                    $"The two adapters are wired to each other "
                    + $"({Confidence.ToString().ToLowerInvariant()} confidence): {basis.Detail}",
                _ =>
                    "Topology not established. Nothing observed proves either a direct cable or a "
                    + "bridge, so results cannot be attributed to one cable.",
            };
        }
    }

    /// <summary>
    /// Combines observations into a verdict.
    /// </summary>
    /// <remarks>
    /// Not a score. Weighing "three of five say switch" against "two say direct" would treat the
    /// signals as votes of equal standing, and they are nothing of the sort: two ports at
    /// different speeds cannot happen on one cable, while a quiet link with no LLDP is exactly
    /// what both topologies look like. Precedence, not arithmetic.
    /// </remarks>
    public static TopologyVerdict From(IEnumerable<TopologyObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var all = observations.ToList();

        // Bridged first, and at any strength. This is the asymmetry: one credible sign of a bridge
        // outweighs any amount of failure to find one, because the signals that can see a bridge
        // are the same ones that go silent when they cannot.
        var bridged = all.Where(o => o.Finding == TopologyFinding.Bridged).ToList();
        if (bridged.Count > 0)
        {
            return new TopologyVerdict(
                TopologyConclusion.Bridged,
                ConfidenceFrom(bridged),
                Ordered(all));
        }

        // Direct needs a positive statement, never the absence of a bridge. A Suggestive signal
        // alone will not do it: the only signals that can positively demonstrate a direct link are
        // the probe that must not cross a bridge and the link-state coupling test, and both are
        // Strong or better when they succeed.
        var direct = all
            .Where(o => o.Finding == TopologyFinding.Direct && o.Strength >= SignalStrength.Strong)
            .ToList();

        if (direct.Count > 0)
        {
            return new TopologyVerdict(
                TopologyConclusion.Direct,
                ConfidenceFrom(direct),
                Ordered(all));
        }

        return new TopologyVerdict(TopologyConclusion.Unknown, TopologyConfidence.None, Ordered(all));
    }

    /// <summary>
    /// Confidence from the strongest agreeing signal, raised when several agree.
    /// </summary>
    /// <remarks>
    /// The strongest signal sets the floor because a conclusive one is conclusive whatever else
    /// was seen. Corroboration lifts a merely suggestive result, since two independent suggestive
    /// signals are worth more than one - but it never lifts anything to High, which is reserved
    /// for a single signal whose alternative is physically impossible.
    /// </remarks>
    private static TopologyConfidence ConfidenceFrom(List<TopologyObservation> agreeing)
    {
        var strongest = agreeing.Max(o => o.Strength);

        return strongest switch
        {
            SignalStrength.Conclusive => TopologyConfidence.High,
            SignalStrength.Strong => TopologyConfidence.Moderate,
            _ when agreeing.Count > 1 => TopologyConfidence.Moderate,
            _ => TopologyConfidence.Low,
        };
    }

    /// <summary>Decisive signals first, so a report leads with what settled it.</summary>
    private static IReadOnlyList<TopologyObservation> Ordered(IEnumerable<TopologyObservation> all) =>
        [.. all
            .OrderBy(o => o.Finding == TopologyFinding.Inconclusive ? 1 : 0)
            .ThenBy(o => o.Strength)
            .ThenBy(o => o.Signal)];
}
