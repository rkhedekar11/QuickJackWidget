namespace QuickJack.Core.Storage;

/// <summary>
/// Where QuickJack keeps its state. See CLAUDE.md "Three stores": the user store is
/// writable by anything running as the user (including the HTTP API); the pinned store
/// lives in ProgramData and is ACL'd admin-write, which is what makes no-prompt
/// elevation defensible.
/// <para>
/// User state lives in <c>%USERPROFILE%\.quickjack</c>, not <c>%APPDATA%</c>: Windows
/// silently redirects AppData writes for any process started from an MSIX-packaged app
/// (the Claude desktop app, for one), so two processes could each see a different
/// api-token and commands.json. The profile root is not virtualised.
/// </para>
/// </summary>
public sealed class QuickJackPaths
{
    public required string UserDirectory { get; init; }
    public required string MachineDirectory { get; init; }

    public string UserCommands => Path.Combine(UserDirectory, "commands.json");
    public string PinnedCommands => Path.Combine(MachineDirectory, "pinned.json");
    public string Settings => Path.Combine(UserDirectory, "settings.json");
    public string ApiToken => Path.Combine(UserDirectory, "api-token");
    public string Endpoint => Path.Combine(UserDirectory, "endpoint.json");
    public string AgentLog => Path.Combine(MachineDirectory, "agent.log");

    public static QuickJackPaths Default { get; } = new()
    {
        UserDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quickjack"),
        MachineDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuickJack"),
    };

    /// <summary>Where user state lived before it moved out of AppData.</summary>
    public static string LegacyUserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickJack");

    /// <summary>All state under one root. Used by tests.</summary>
    public static QuickJackPaths Under(string root) => new()
    {
        UserDirectory = Path.Combine(root, "user"),
        MachineDirectory = Path.Combine(root, "machine"),
    };

    public void EnsureUserDirectory() => Directory.CreateDirectory(UserDirectory);

    /// <summary>
    /// Carries commands and settings over from <paramref name="legacyDirectory"/> the first
    /// time the new directory is used. The token is deliberately not copied: a copy would
    /// not keep its restricted ACL, and a fresh one is created with it.
    /// </summary>
    public void MigrateFrom(string legacyDirectory)
    {
        if (!Directory.Exists(legacyDirectory)) return;
        EnsureUserDirectory();

        foreach (var name in new[] { "commands.json", "settings.json" })
        {
            var source = Path.Combine(legacyDirectory, name);
            var target = Path.Combine(UserDirectory, name);
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }
    }
}
