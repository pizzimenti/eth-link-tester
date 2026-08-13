namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Thrown when an adapter that was present earlier no longer exists.
/// </summary>
/// <remarks>
/// Distinct from a general failure because it is the one case that retrying cannot fix. A USB
/// adapter unplugged mid-run leaves a restore entry that can never succeed; treating that as an
/// ordinary error would keep it in the journal forever and show the user a permanent, unclearable
/// alarm about hardware they have already removed.
/// </remarks>
public sealed class AdapterNotFoundException : InvalidOperationException
{
    public AdapterNotFoundException(string adapterId)
        : base($"No adapter with id '{adapterId}' is present.") => AdapterId = adapterId;

    public AdapterNotFoundException(string adapterId, string message)
        : base(message) => AdapterId = adapterId;

    public AdapterNotFoundException()
        : base("The adapter is no longer present.") => AdapterId = string.Empty;

    public AdapterNotFoundException(string message, Exception innerException)
        : base(message, innerException) => AdapterId = string.Empty;

    public string AdapterId { get; }
}
