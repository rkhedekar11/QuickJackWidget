namespace QuickJack.Core.Models;

/// <summary>Which interpreter runs the script.</summary>
public enum ShellKind
{
    PowerShell, // Windows PowerShell 5.1 (powershell.exe)
    Pwsh,       // PowerShell 7+ (pwsh.exe)
    Cmd,        // cmd.exe
}

/// <summary>
/// How a command acquires administrator rights. See CLAUDE.md "Trust boundary" —
/// <see cref="Agent"/> is the only mode the HTTP API may never create.
/// </summary>
public enum ElevationMode
{
    None,  // runs as the current (filtered) token, no prompt
    Uac,   // ShellExecute verb=runas, Windows consent dialog every run
    Agent, // dispatched to the elevated agent, no prompt; UI-pinned only
}

/// <summary>How the command's output reaches the user.</summary>
public enum OutputMode
{
    Capture,     // stdout/stderr streamed into the widget's output panel
    Interactive, // a real console window the user can type into
}

/// <summary>Which store a command was loaded from.</summary>
public enum CommandOrigin
{
    User,   // %APPDATA%\QuickJack\commands.json  — user-writable
    Pinned, // %ProgramData%\QuickJack\pinned.json — admin-writable
}
