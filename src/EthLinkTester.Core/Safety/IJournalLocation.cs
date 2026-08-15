namespace EthLinkTester.Core.Safety;

/// <summary>
/// Guarantees that only administrators can write where the journal lives.
/// </summary>
/// <remarks>
/// <para>
/// The journal's value comes from a single claim: a non-empty journal at startup is proof that a
/// previous run did not clean up after itself, so its contents can be applied to hardware without
/// asking. That claim holds only if nobody else can put entries there.
/// </para>
/// <para>
/// The default location under <c>%ProgramData%</c> inherits <c>BUILTIN\Users:(WD,AD)</c> from its
/// parent - Windows grants every standard user write and append there. An append-only JSONL file
/// is then the easiest possible thing to attack: one well-formed line at the end, no need to
/// parse or rewrite anything, and an elevated process applies it to NIC configuration on the next
/// launch. The directory's permissions are what make the journal safe, so they are established
/// before it is used rather than assumed.
/// </para>
/// <para>
/// An owner check would not have caught this. The app creates the file and therefore owns it; an
/// attacker appending a line does not change that. Only denying the write works.
/// </para>
/// </remarks>
public interface IJournalLocation
{
    /// <summary>
    /// Creates the directory if needed and restricts writes to administrators and SYSTEM.
    /// </summary>
    /// <exception cref="IOException">
    /// Thrown when the restriction cannot be applied. Callers must treat that as fatal to the
    /// journal: an unprotected journal is not a weaker safety net, it is an attack surface that
    /// writes to hardware.
    /// </exception>
    void Secure(string directoryPath);

    /// <summary>
    /// Whether the directory currently denies write access to ordinary users.
    /// </summary>
    bool IsProtected(string directoryPath);
}
