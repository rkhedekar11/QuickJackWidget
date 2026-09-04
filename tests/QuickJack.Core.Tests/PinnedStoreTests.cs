using System.Security.AccessControl;
using System.Security.Principal;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

/// <summary>
/// The admin-owned store. Its contents are ordinary; what matters is
/// <see cref="PinnedStore.IsDirectorySecured"/>, which is the check standing between the
/// agent and a store any process running as the user could write to.
/// </summary>
public class PinnedStoreTests
{
    private static CommandDef Command(string id) => new()
    {
        Id = id,
        Name = id,
        Script = "echo hi",
        Elevation = ElevationMode.Agent,
    };

    [Fact]
    public void A_missing_store_reads_as_empty()
    {
        using var root = new TempRoot();

        Assert.Empty(PinnedStore.Read(root.Paths));
    }

    [Fact]
    public void Written_commands_read_back_as_pinned()
    {
        using var root = new TempRoot();
        PinnedStore.Write(root.Paths, [Command("flush-dns")]);

        var command = Assert.Single(PinnedStore.Read(root.Paths));

        Assert.Equal("flush-dns", command.Id);
        Assert.Equal(ElevationMode.Agent, command.Elevation);

        // Origin is not persisted; it is stamped on load, and it is what CommandRunner
        // checks before letting an agent command run at all.
        Assert.Equal(CommandOrigin.Pinned, command.Origin);
    }

    [Fact]
    public void A_command_written_as_user_origin_still_reads_back_as_pinned()
    {
        using var root = new TempRoot();
        PinnedStore.Write(root.Paths, [Command("flush-dns") with { Origin = CommandOrigin.User }]);

        Assert.Equal(CommandOrigin.Pinned, PinnedStore.Read(root.Paths)[0].Origin);
    }

    [Fact]
    public void Find_matches_ids_case_insensitively()
    {
        using var root = new TempRoot();
        PinnedStore.Write(root.Paths, [Command("flush-dns")]);

        Assert.NotNull(PinnedStore.Find(root.Paths, "FLUSH-DNS"));
        Assert.Null(PinnedStore.Find(root.Paths, "something-else"));
    }

    [Fact]
    public void Writing_replaces_the_whole_store()
    {
        using var root = new TempRoot();
        PinnedStore.Write(root.Paths, [Command("one"), Command("two")]);
        PinnedStore.Write(root.Paths, [Command("three")]);

        Assert.Equal(["three"], PinnedStore.Read(root.Paths).Select(c => c.Id));
    }

    // ---- the recorded client ----

    [Fact]
    public void No_recorded_client_path_reads_as_null()
    {
        using var root = new TempRoot();

        Assert.Null(PinnedStore.ReadAllowedClientPath(root.Paths));
    }

    [Fact]
    public void A_recorded_client_path_reads_back_trimmed()
    {
        using var root = new TempRoot();
        File.WriteAllText(PinnedStore.ClientPathFile(root.Paths), "  C:\\App\\QuickJack.exe\r\n");

        Assert.Equal("C:\\App\\QuickJack.exe", PinnedStore.ReadAllowedClientPath(root.Paths));
    }

    [Fact]
    public void The_recorded_client_path_lives_in_the_admin_owned_directory()
    {
        // If it lived beside the user store, the widget could rewrite it to name any
        // executable and the client check would be worth nothing.
        using var root = new TempRoot();

        Assert.Equal(
            root.Paths.MachineDirectory,
            Path.GetDirectoryName(PinnedStore.ClientPathFile(root.Paths)));
    }

    // ---- the lockdown check ----

    [Fact]
    public void A_directory_that_does_not_exist_is_not_secured()
    {
        var paths = QuickJackPaths.Under(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(PinnedStore.IsDirectorySecured(paths));
    }

    [Fact]
    public void An_ordinary_user_created_directory_is_not_secured()
    {
        // What a first run produces before the installer has been through: inherited
        // permissions, owned by the user. The agent must refuse to serve from it.
        using var root = new TempRoot();

        Assert.False(PinnedStore.IsDirectorySecured(root.Paths));
    }

    [Fact]
    public void Blocking_inheritance_alone_does_not_make_a_directory_secured()
    {
        // The owner of a directory can always rewrite its ACL, so a locked-down ACL on a
        // user-owned directory protects nothing at all.
        using var root = new TempRoot();

        var directory = new DirectoryInfo(root.Paths.MachineDirectory);
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        directory.SetAccessControl(security);

        Assert.True(directory.GetAccessControl().AreAccessRulesProtected);
        Assert.False(PinnedStore.IsDirectorySecured(root.Paths));
    }

    [Fact]
    public void The_current_user_owns_a_directory_they_created()
    {
        // Guards the assumption the test above rests on. Producing the positive case — an
        // Administrators-owned directory — needs elevation, so it stays on the manual
        // checklist in TODO.md.
        using var root = new TempRoot();

        var owner = new DirectoryInfo(root.Paths.MachineDirectory)
            .GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        Assert.Equal(WindowsIdentity.GetCurrent().User, owner);
        Assert.NotEqual(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), owner);
    }
}
