using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>Maps a <see cref="ShellKind"/> onto an executable and its argument list.</summary>
public static class ShellInfo
{
    public static string Executable(ShellKind shell) => shell switch
    {
        ShellKind.Cmd => "cmd.exe",
        ShellKind.Pwsh => "pwsh.exe",
        _ => "powershell.exe",
    };

    /// <summary>Arguments that run <paramref name="scriptPath"/> and propagate its exit code.</summary>
    public static IReadOnlyList<string> Arguments(ShellKind shell, string scriptPath) => shell switch
    {
        // /d skips AutoRun commands from the registry, which would otherwise inject output
        // (and arbitrary code) into every single run.
        ShellKind.Cmd => ["/d", "/c", scriptPath],

        // -NonInteractive so a script that unexpectedly prompts fails fast instead of
        // hanging forever against a stdin nobody is attached to.
        _ => ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
    };

    /// <summary>True if the interpreter is actually present on this machine.</summary>
    public static bool IsAvailable(ShellKind shell) => Resolve(shell) is not null;

    /// <summary>Full path to the interpreter, or null if it is not on PATH.</summary>
    public static string? Resolve(ShellKind shell)
    {
        var exe = Executable(shell);

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wellKnown = shell switch
        {
            ShellKind.Cmd => Path.Combine(systemRoot, "cmd.exe"),
            ShellKind.PowerShell => Path.Combine(systemRoot, "WindowsPowerShell", "v1.0", "powershell.exe"),
            _ => null,
        };
        if (wellKnown is not null && File.Exists(wellKnown)) return wellKnown;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry is not our problem */ }
        }

        return null;
    }
}
