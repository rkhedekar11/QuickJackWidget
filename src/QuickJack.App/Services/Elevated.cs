using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace QuickJack.App.Services;

/// <summary>Outcome of something the widget asked an administrator to do.</summary>
public sealed record InstallResult(bool Success, string Message);

/// <summary>
/// Runs a helper elevated, one UAC prompt per call, and waits for it.
/// <para>
/// Every privileged thing the widget does goes through here: installing the agent, and
/// pinning or unpinning a command. The prompt is not an obstacle to work around — it is the
/// thing that makes a no-prompt admin command something the user granted deliberately.
/// </para>
/// </summary>
public static class Elevated
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — the user dismissed the dialog

    public static InstallResult RunAndWait(string exe, string[] args, int timeoutMs = 60_000)
    {
        try
        {
            using var process = Start(exe, args, elevated: true, capture: false);
            if (process is null) return new InstallResult(false, $"Could not start {exe}.");

            process.WaitForExit(timeoutMs);

            return process.ExitCode == 0
                ? new InstallResult(true, string.Empty)
                : new InstallResult(false, Describe(exe, process.ExitCode));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // Declining the prompt is a decision, not a failure.
            return new InstallResult(false, "Cancelled: administrator consent was declined.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new InstallResult(false, ex.Message);
        }
    }

    public static Process? Start(string exe, string[] args, bool elevated, bool capture)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = elevated,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = capture && !elevated,
            RedirectStandardError = capture && !elevated,
        };

        if (elevated) psi.Verb = "runas";
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return Process.Start(psi);
    }

    /// <summary>Path of a sibling executable shipped alongside the widget.</summary>
    public static string? SiblingExecutable(string fileName)
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        return directory is null ? null : Path.Combine(directory, fileName);
    }

    private static string Describe(string exe, int exitCode) => exitCode switch
    {
        // The exit codes QuickJack.Agent uses for its one-shot modes.
        2 => "That needs administrator, and the elevated helper did not get it.",
        3 => "The command was not found.",
        _ => $"{Path.GetFileName(exe)} exited with code {exitCode}.",
    };
}
