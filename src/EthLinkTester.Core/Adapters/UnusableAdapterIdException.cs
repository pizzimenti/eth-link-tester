namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Thrown when an adapter id is not one this app can address at all.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than a bare <see cref="ArgumentException"/> so restore can tell "this entry
/// can never work" from "this call was made wrongly". A journal entry naming a malformed id passes
/// JSON validation but no write built from it can ever succeed, so it must be abandoned like
/// vanished hardware - while an ordinary argument mistake elsewhere must still surface as a bug.
/// Catching every <see cref="ArgumentException"/> would have swallowed both.
/// </para>
/// <para>
/// Derives from <see cref="ArgumentException"/> because that remains the correct contract for a
/// caller passing a bad id directly.
/// </para>
/// </remarks>
public sealed class UnusableAdapterIdException : ArgumentException
{
    public UnusableAdapterIdException()
        : base("The adapter id is not usable.")
    {
    }

    public UnusableAdapterIdException(string message)
        : base(message)
    {
    }

    public UnusableAdapterIdException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnusableAdapterIdException(string message, string paramName)
        : base(message, paramName)
    {
    }

    /// <summary>The intended way to construct this: names the id that cannot be used.</summary>
    public static UnusableAdapterIdException ForId(string adapterId, string parameterName) =>
        new($"Adapter id '{adapterId}' is not an interface GUID.", parameterName);
}
