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

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
