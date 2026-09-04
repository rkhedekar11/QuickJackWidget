using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

/// <summary>
/// The check that decides whether the agent may be installed at all: a scheduled task with
/// highest privileges runs whatever is at its path, so that path had better not be one an
/// ordinary user can write to.
/// </summary>
public class WindowsAclTests
{
    private static readonly string ProgramFiles =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [Fact]
    public void A_directory_the_user_just_created_is_writable_by_non_administrators()
    {
        using var root = new TempRoot();

        Assert.True(WindowsAcl.IsWritableByNonAdministrators(root.Path));
    }

    [Fact]
    public void A_file_the_user_just_created_is_writable_by_non_administrators()
    {
        using var root = new TempRoot();
        var file = Path.Combine(root.Path, "agent.exe");
        File.WriteAllText(file, "not really an executable");

        Assert.True(WindowsAcl.IsWritableByNonAdministrators(file));
    }

    [Fact]
    public void Program_files_is_not_writable_by_non_administrators()
    {
        // The one location the agent actually wants to be installed in. It is owned and
        // written by TrustedInstaller rather than by Administrators, which is why the check
        // cannot simply look for the Administrators group.
        Assert.False(WindowsAcl.IsWritableByNonAdministrators(ProgramFiles));
    }

    [Fact]
    public void The_windows_directory_is_not_writable_by_non_administrators()
    {
        Assert.False(WindowsAcl.IsWritableByNonAdministrators(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    }

    [Fact]
    public void A_path_that_does_not_exist_is_treated_as_writable()
    {
        // Erring towards refusing to install: not installing is recoverable, installing on
        // top of something unreadable is not.
        Assert.True(WindowsAcl.IsWritableByNonAdministrators(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nothing.exe")));
    }

    [Fact]
    public void The_user_profile_is_writable_by_non_administrators()
    {
        // %LOCALAPPDATA% is where a per-user install would land, and is precisely the case
        // the agent must refuse: the user can replace the binary there at will.
        Assert.True(WindowsAcl.IsWritableByNonAdministrators(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }
}
