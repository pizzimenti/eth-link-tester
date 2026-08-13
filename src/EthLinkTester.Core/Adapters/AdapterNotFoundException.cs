namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Thrown when an adapter that was present earlier no longer exists.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from a general failure because it is the one case that retrying cannot fix. A USB
/// adapter unplugged mid-run leaves a restore entry that can never succeed; treating that as an
/// ordinary error would keep it in the journal forever and show the user a permanent, unclearable
/// alarm about hardware they have already removed.
/// </para>
/// <para>
/// Construct it with <see cref="ForAdapter"/>. The constructors take a <em>message</em>, as the
/// framework convention requires - an earlier version overloaded them so that a bare string meant
/// an adapter id in one and a message in another, which made
/// <c>new AdapterNotFoundException("Ethernet is gone")</c> silently record that sentence as the
/// adapter's identity.
/// </para>
/// </remarks>
public sealed class AdapterNotFoundException : InvalidOperationException
{
    public AdapterNotFoundException()
        : base("The adapter is no longer present.") => AdapterId = string.Empty;

    public AdapterNotFoundException(string message)
        : base(message) => AdapterId = string.Empty;

    public AdapterNotFoundException(string message, Exception innerException)
        : base(message, innerException) => AdapterId = string.Empty;

    private AdapterNotFoundException(string message, string adapterId)
        : base(message) => AdapterId = adapterId;

    /// <summary>The adapter that is gone, when it is known. Empty otherwise.</summary>
    public string AdapterId { get; }

    /// <summary>The intended way to construct this: names the adapter that vanished.</summary>
    public static AdapterNotFoundException ForAdapter(string adapterId) =>
        new($"No adapter with id '{adapterId}' is present.", adapterId);
}
