namespace EthLinkTester.Core.Safety;

/// <summary>
/// Which journal entries a restore pass is entitled to put back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Restore used to be global only, and that does not compose with probes.</b> A topology probe
/// forces a speed, reads the result, and has to put it back within seconds - inside a run that may
/// have changed several other properties on both adapters and is not finished with them. A global
/// restore there reverts all of it: the probe's own force, the offload settings the run neutralised,
/// and anything a concurrent run recorded, all silently, leaving the suite measuring a rig it no
/// longer configured.
/// </para>
/// <para>
/// The journal itself was always fine with this. Entries are removed by identity precisely so a
/// concurrent run's records survive a restore pass; only the selection was missing.
/// </para>
/// <para>
/// Startup recovery stays <see cref="Everything"/>, and it must: a non-empty journal at launch is
/// proof of a run that died, and there is nobody left who knows which entries were whose.
/// </para>
/// </remarks>
public sealed record RestoreScope
{
    private readonly string? _adapterId;
    private readonly string? _keyword;

    private RestoreScope(string? adapterId, string? keyword)
    {
        _adapterId = adapterId;
        _keyword = keyword;
    }

    /// <summary>
    /// Every pending entry. The startup-recovery scope, and the end-of-run scope.
    /// </summary>
    public static RestoreScope Everything { get; } = new(null, null);

    /// <summary>Everything this app changed on one adapter.</summary>
    public static RestoreScope Adapter(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        return new RestoreScope(adapterId, null);
    }

    /// <summary>
    /// One property on one adapter - what a probe undoing its own change should ask for.
    /// </summary>
    public static RestoreScope Property(string adapterId, string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        return new RestoreScope(adapterId, keyword);
    }

    /// <summary>
    /// True for the unrestricted scope.
    /// </summary>
    /// <remarks>
    /// Read by the restore pass for one decision beyond selection: an unreadable journal is
    /// discarded wholesale, and that is a recovery action rather than a restore. A probe putting one
    /// property back has no business throwing away records it cannot read and did not write.
    /// </remarks>
    public bool IsEverything => _adapterId is null;

    /// <summary>Whether this scope covers a given entry.</summary>
    public bool Includes(PendingRestore entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_adapterId is not null
            && !string.Equals(entry.AdapterId, _adapterId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _keyword is null
            || string.Equals(entry.PropertyKeyword, _keyword, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => (_adapterId, _keyword) switch
    {
        (null, _) => "everything pending",
        (var adapter, null) => $"everything on {adapter}",
        var (adapter, keyword) => $"{keyword} on {adapter}",
    };
}
