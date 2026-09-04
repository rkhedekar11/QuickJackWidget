using System.Security.AccessControl;
using System.Security.Principal;
using QuickJack.Core.Models;

namespace QuickJack.Core.Storage;

/// <summary>
/// The admin-owned command store: the only place an <see cref="ElevationMode.Agent"/>
/// command may live.
/// <para>
/// Its directory is ACL'd so Administrators can write and Users can only read. That is the
/// property the whole no-prompt-elevation design rests on: because planting a pinned command
/// requires administrator, doing so costs a UAC prompt, and a process running merely as the
/// user cannot give itself a silent admin button.
/// </para>
/// </summary>
public static class PinnedStore
{
    public static IReadOnlyList<CommandDef> Read(QuickJackPaths paths)
    {
        try
        {
            return CommandFileIo.Read(paths.PinnedCommands).Commands
                .Select(c => c with { Origin = CommandOrigin.Pinned })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static CommandDef? Find(QuickJackPaths paths, string id) =>
        Read(paths).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the pinned store. Requires administrator once the directory ACL is in place —
    /// which is the point.
    /// </summary>
    public static void Write(QuickJackPaths paths, IEnumerable<CommandDef> commands)
    {
        Directory.CreateDirectory(paths.MachineDirectory);

        CommandFileIo.Write(paths.PinnedCommands, new CommandFile
        {
            Commands = commands.Select(c => c with { Origin = CommandOrigin.Pinned }).ToList(),
        });
    }

    /// <summary>
    /// Adds or replaces one command in the pinned store, marking it for no-prompt elevation.
    /// Requires administrator, which is exactly the cost this design puts on creating a
    /// button that runs as admin without asking.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The command has not been approved. An unapproved command is one someone else
    /// registered and the user has never looked at; promoting it straight to silent
    /// administrator is the escalation the approval gate exists to prevent.
    /// </exception>
    public static CommandDef Pin(QuickJackPaths paths, CommandDef command)
    {
        if (!command.Approved)
        {
            throw new InvalidOperationException(
                $"'{command.Name}' has not been approved yet, so it cannot be pinned.");
        }

        // Read through CommandFileIo, not Read(): that one turns an unreadable store into an
        // empty list, which here would silently drop every other pinned command.
        var file = CommandFileIo.Read(paths.PinnedCommands);

        var entry = command with
        {
            Elevation = ElevationMode.Agent,
            Origin = CommandOrigin.Pinned,
            Approved = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var index = file.Commands.FindIndex(c => IdEquals(c.Id, command.Id));
        if (index >= 0) file.Commands[index] = entry;
        else file.Commands.Add(entry);

        Directory.CreateDirectory(paths.MachineDirectory);
        CommandFileIo.Write(paths.PinnedCommands, file);
        return entry;
    }

    /// <summary>Removes a command from the pinned store. Requires administrator.</summary>
    /// <returns>False if it was not pinned in the first place.</returns>
    public static bool Unpin(QuickJackPaths paths, string id)
    {
        var file = CommandFileIo.Read(paths.PinnedCommands);
        if (file.Commands.RemoveAll(c => IdEquals(c.Id, id)) == 0) return false;

        CommandFileIo.Write(paths.PinnedCommands, file);
        return true;
    }

    private static bool IdEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Locks the machine directory to Administrators-write / Users-read. Must be called from
    /// an elevated process; it is part of agent installation.
    /// </summary>
    public static void SecureDirectory(QuickJackPaths paths)
    {
        Directory.CreateDirectory(paths.MachineDirectory);

        var security = new DirectorySecurity();

        // Inheritance off, so nothing broader from ProgramData leaks in.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        // Read only. Without this the widget could rewrite its own pinned commands and the
        // UAC prompt that gates them would be worth nothing.
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        // Owner is set to Administrators as well: an owner can always rewrite the ACL, so
        // leaving the installing user as owner would leave a way back in.
        security.SetOwner(administrators);

        new DirectoryInfo(paths.MachineDirectory).SetAccessControl(security);
    }

    /// <summary>
    /// True if the machine directory is actually locked down. The agent refuses to run
    /// anything when it is not — an unprotected pinned store is writable by any process
    /// running as the user, which would make the agent a free privilege escalation.
    /// </summary>
    public static bool IsDirectorySecured(QuickJackPaths paths)
    {
        try
        {
            if (!Directory.Exists(paths.MachineDirectory)) return false;

            var security = new DirectoryInfo(paths.MachineDirectory).GetAccessControl();
            if (!security.AreAccessRulesProtected) return false;

            // An owner can always rewrite the DACL, so a locked-down ACL on a
            // user-owned directory protects nothing. This must be checked as well.
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null) return false;

            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (!owner.Equals(administratorsSid) && !owner.Equals(systemSid)) return false;

            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

            const FileSystemRights writes =
                FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.CreateFiles | FileSystemRights.Delete;

            foreach (FileSystemAccessRule rule in
                     security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!rule.IdentityReference.Equals(users) && !rule.IdentityReference.Equals(everyone)) continue;
                if ((rule.FileSystemRights & writes) != 0) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>
    /// Path of the executable the agent will accept connections from, recorded at install
    /// time in the admin-owned directory so the widget cannot change it afterwards.
    /// </summary>
    public static string ClientPathFile(QuickJackPaths paths) =>
        Path.Combine(paths.MachineDirectory, "client-path.txt");

    public static string? ReadAllowedClientPath(QuickJackPaths paths)
    {
        try
        {
            var file = ClientPathFile(paths);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
