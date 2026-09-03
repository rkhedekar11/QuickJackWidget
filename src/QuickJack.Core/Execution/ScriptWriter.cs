using System.Text;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>A script on disk, deleted on dispose.</summary>
public sealed class TempScript(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Writes a command's script to a temp file.
/// <para>
/// Scripts always go through a file rather than <c>-Command</c> / <c>/c "..."</c>: it
/// sidesteps quoting and multi-line problems entirely, and keeps user script text off any
/// command line where another process could read it.
/// </para>
/// </summary>
public static class ScriptWriter
{
    // Windows PowerShell 5.1 assumes the ANSI code page for a .ps1 without a BOM, so
    // non-ASCII script text is mangled unless one is present.
    private static readonly Encoding PowerShellEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    // cmd.exe, by contrast, chokes on a BOM: it tries to run it as part of the first line.
    private static readonly Encoding CmdEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Directory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickJack");

    public static TempScript Write(ShellKind shell, string script, string? runId = null)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var name = $"qj-{runId ?? Guid.NewGuid().ToString("N")[..8]}{Extension(shell)}";
        var path = System.IO.Path.Combine(Directory, name);

        var (body, encoding) = shell switch
        {
            ShellKind.Cmd => (CmdPreamble + script, CmdEncoding),
            _ => (PowerShellPreamble + script, PowerShellEncoding),
        };

        File.WriteAllText(path, body, encoding);
        return new TempScript(path);
    }

    public static string Extension(ShellKind shell) => shell == ShellKind.Cmd ? ".cmd" : ".ps1";

    /// <summary>
    /// PowerShell writes stdout using <c>[Console]::OutputEncoding</c>, which defaults to the
    /// OEM code page — so without this the parent decodes UTF-8 bytes that were never sent.
    /// </summary>
    private const string PowerShellPreamble = """
        try {
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
        } catch { }

        """;

    /// <summary>
    /// Same encoding problem, cmd's answer — plus delayed expansion, which is a security
    /// requirement here rather than a convenience. cmd expands <c>%VAR%</c> before parsing
    /// the line, so a parameter value containing <c>&amp;</c> or <c>|</c> would be executed
    /// as a second command. <c>!VAR!</c> expands after parsing and is inert.
    /// <see cref="ParameterBinder"/> refuses to bind a cmd script that uses the
    /// <c>%QJ_NAME%</c> form.
    /// </summary>
    private const string CmdPreamble = """
        @echo off
        chcp 65001>nul
        setlocal enabledelayedexpansion

        """;

    /// <summary>Best-effort sweep of scripts left behind by a crash.</summary>
    public static void CleanStale(TimeSpan olderThan)
    {
        if (!System.IO.Directory.Exists(Directory)) return;

        var cutoff = DateTime.UtcNow - olderThan;
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "qj-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
