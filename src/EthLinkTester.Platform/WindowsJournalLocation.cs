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
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or IdentityNotMappedException or SystemException)
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

        var rules = new DirectoryInfo(directoryPath)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

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
    /// AppendData is the one that matters and the one easiest to overlook: the journal is
    /// append-only, so append alone is a complete attack.
    /// </remarks>
    private static bool GrantsWrite(FileSystemRights rights) =>
        (rights & (FileSystemRights.WriteData
                   | FileSystemRights.AppendData
                   | FileSystemRights.Delete
                   | FileSystemRights.ChangePermissions
                   | FileSystemRights.TakeOwnership)) != 0;

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        // CREATOR OWNER resolves to whoever creates an item; under an administrators-only
        // directory that is an administrator, so it grants nothing extra.
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid);
}
