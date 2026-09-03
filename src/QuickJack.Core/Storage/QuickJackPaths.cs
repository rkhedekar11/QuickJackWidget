namespace QuickJack.Core.Storage;

/// <summary>
/// Where QuickJack keeps its state. See CLAUDE.md "Three stores": the user store is
/// writable by anything running as the user (including the HTTP API); the pinned store
/// lives in ProgramData and is ACL'd admin-write, which is what makes no-prompt
/// elevation defensible.
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickJack"),
        MachineDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuickJack"),
    };

    /// <summary>All state under one root. Used by tests.</summary>
    public static QuickJackPaths Under(string root) => new()
    {
        UserDirectory = Path.Combine(root, "user"),
        MachineDirectory = Path.Combine(root, "machine"),
    };

    public void EnsureUserDirectory() => Directory.CreateDirectory(UserDirectory);
}
