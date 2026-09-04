using System.Security.AccessControl;
using System.Security.Principal;

namespace QuickJack.Core.Storage;

/// <summary>Locks files and directories down to the current user.</summary>
public static class WindowsAcl
{
    /// <summary>
    /// Replaces the ACL with a single entry granting the current user full control, and
    /// disables inheritance so nothing broader leaks in from the parent. Used for the API
    /// token, whose whole value as a credential is that other accounts cannot read it.
    /// </summary>
    public static void RestrictToCurrentUser(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("Could not determine the current user SID.");

        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            var file = new FileInfo(path);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
    }

    /// <summary>Same, but never throws — for paths where locking down is a nicety, not the point.</summary>
    public static bool TryRestrictToCurrentUser(string path)
    {
        try
        {
            RestrictToCurrentUser(path);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or InvalidOperationException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>
    /// True if a process running as an ordinary user could write to <paramref name="path"/>.
    /// <para>
    /// This is the check that decides whether the agent may be installed at all. A scheduled
    /// task with "run with highest privileges" runs <em>whatever is at that path</em>, so if
    /// anything running as the user can replace the executable, the agent stops being a
    /// deliberate grant and becomes a free escalation to high integrity at next logon.
    /// A recorded path or hash does not fix this — an attacker who can swap the binary swaps
    /// it at the same path. Only the ACL does.
    /// </para>
    /// <para>
    /// Errs towards saying yes: a path whose ACL cannot be read is treated as writable,
    /// because refusing to install is recoverable and installing wrongly is not.
    /// </para>
    /// </summary>
    public static bool IsWritableByNonAdministrators(string path)
    {
        try
        {
            var rules = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                    .GetAccessRules(true, true, typeof(SecurityIdentifier))
                : new FileInfo(path).GetAccessControl()
                    .GetAccessRules(true, true, typeof(SecurityIdentifier));

            const FileSystemRights writes =
                FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.CreateFiles | FileSystemRights.Delete |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & writes) == 0) continue;

                if (rule.IdentityReference is SecurityIdentifier sid && !IsAdministrative(sid))
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PrivilegeNotHeldException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether write access for this identity is already administrator-equivalent. Program
    /// Files is owned and written by TrustedInstaller rather than Administrators, so a check
    /// that recognised only the Administrators group would reject the one location the agent
    /// most wants to be installed in.
    /// </summary>
    private static bool IsAdministrative(SecurityIdentifier sid)
    {
        if (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;

        // NT SERVICE\TrustedInstaller, and CREATOR OWNER, which only ever grants rights on
        // objects created later by whoever already had permission to create them.
        return sid.Value is "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
                         or "S-1-3-0"
            || sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal);
    }

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
