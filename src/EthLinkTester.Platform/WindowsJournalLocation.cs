using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EthLinkTester.Core.Safety;

namespace EthLinkTester.Platform;

/// <summary>
/// Restricts the journal directory to administrators by replacing its inherited permissions.
/// </summary>
/// <remarks>
/// <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> write and append with container and object
/// inheritance, so anything created beneath it is writable by every standard user unless that
/// inheritance is broken. Measured on the reference machine before this existed:
/// <c>BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)</c>.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsJournalLocation : IJournalLocation
{
    public void Secure(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        try
        {
            var directory = Directory.CreateDirectory(directoryPath);
            var security = new DirectorySecurity();

            // Inheritance off and existing rules discarded, rather than adding a deny. A deny ACE
            // would sit alongside the inherited allow and depend on evaluation order; removing the
            // inheritance removes the grant itself.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var identity in new[]
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            directory.SetAccessControl(security);

            // Read back rather than assume. A security control that reports success without
            // checking is the kind that fails silently for years, and the cost of confirming is
            // one ACL read against a directory this app is about to trust with hardware writes.
            if (!IsProtected(directoryPath))
            {
                throw new IOException(
                    $"'{directoryPath}' still grants write access to non-administrators after " +
                    "its permissions were replaced.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or IdentityNotMappedException or IOException)
        {
            throw new IOException(
                $"Could not restrict '{directoryPath}' to administrators, so the restore journal " +
                "cannot be trusted. Entries there would be applied to network hardware on the " +
                "next launch, and any user able to write to that directory could choose them.",
                ex);
        }
    }

    public bool IsProtected(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        var security = new DirectoryInfo(directoryPath).GetAccessControl();

        // Inheritance still enabled means the parent can hand out rights at any time, so the
        // current rule set proves nothing about the next one.
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        // An untrusted owner can rewrite the DACL regardless of what it currently says.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !IsTrusted(owner))
        {
            return false;
        }

        var rules = security.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || !GrantsWrite(rule.FileSystemRights))
            {
                continue;
            }

            if (rule.IdentityReference is SecurityIdentifier sid && !IsTrusted(sid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Any right that could add or alter a journal entry.
    /// </summary>
    /// <remarks>
    /// WriteData is the one that matters most and the one easiest to overlook. On a directory it
    /// means "create a file", and the journal is deleted after every clean restore - so an
    /// attacker does not need to modify an existing file, only to create one while none exists,
    /// which is the usual state. The full inherited grant this replaces was (WD,AD,WEA,WA), so
    /// all four are checked.
    /// </remarks>
    private static bool GrantsWrite(FileSystemRights rights) =>
        (rights & (FileSystemRights.WriteData
                   | FileSystemRights.AppendData
                   | FileSystemRights.WriteAttributes
                   | FileSystemRights.WriteExtendedAttributes
                   | FileSystemRights.Delete
                   | FileSystemRights.DeleteSubdirectoriesAndFiles
                   | FileSystemRights.ChangePermissions
                   | FileSystemRights.TakeOwnership)) != 0;

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        // CREATOR OWNER resolves to whoever creates an item; under an administrators-only
        // directory that is an administrator, so it grants nothing extra.
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid);
}
