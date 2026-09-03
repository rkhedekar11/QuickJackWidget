using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>
/// Opens a real console window and leaves it open.
/// <para>
/// Inline capture cannot serve a script that prompts for input or drops into a REPL — there
/// is nothing for the user to type into. This mode gives up output capture in exchange for
/// a terminal the user actually owns.
/// </para>
/// </summary>
public sealed class InteractiveRunner : ICommandRunner
{
    private const int ErrorCancelled = 1223;

    public RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var bound = ParameterBinder.Bind(command, arguments);

        var runId = RunHandle.NewRunId();
        var channel = Channel.CreateUnbounded<OutputLine>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var completion = Task.FromResult(Launch(command, bound, runId, channel.Writer));
        return new RunHandle(runId, command, channel.Reader, completion, cts);
    }

    private static RunResult Launch(
        CommandDef command, BoundScript bound, string runId, ChannelWriter<OutputLine> writer)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var executable = ShellInfo.Resolve(command.Shell);
            if (executable is null)
            {
                writer.TryWrite(OutputLine.Err($"{ShellInfo.Executable(command.Shell)} was not found."));
                return new RunResult(RunState.Faulted, null, Stopwatch.GetElapsedTime(started));
            }

            // The script outlives this call, so it cannot be deleted on the way out. The
            // stale sweep on startup is what eventually clears these.
            var script = ScriptWriter.Write(command.Shell, bound.Script, runId);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };

            if (command.Elevation != ElevationMode.None) psi.Verb = "runas";

            foreach (var arg in InteractiveArguments(command.Shell, script.Path))
                psi.ArgumentList.Add(arg);

            if (!string.IsNullOrWhiteSpace(command.WorkingDirectory) &&
                Directory.Exists(command.WorkingDirectory))
            {
                psi.WorkingDirectory = command.WorkingDirectory;
            }

            // UseShellExecute means the child inherits this process's environment block, so
            // parameters have to be set here rather than on the ProcessStartInfo.
            foreach (var (key, value) in bound.Environment)
                Environment.SetEnvironmentVariable(key, value);

            using var process = Process.Start(psi);

            writer.TryWrite(OutputLine.Info("Launched in a console window; output is not captured."));
            writer.TryComplete();
            return new RunResult(RunState.Succeeded, null, Stopwatch.GetElapsedTime(started));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            writer.TryWrite(OutputLine.Info("Cancelled: administrator consent was declined."));
            writer.TryComplete();
            return new RunResult(RunState.Cancelled, null, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            writer.TryWrite(OutputLine.Err(ex.Message));
            writer.TryComplete();
            return new RunResult(RunState.Faulted, null, Stopwatch.GetElapsedTime(started), ex.Message);
        }
    }

    /// <summary>Same as a captured run, but the window stays open afterwards.</summary>
    private static IReadOnlyList<string> InteractiveArguments(ShellKind shell, string scriptPath) =>
        shell switch
        {
            ShellKind.Cmd => ["/d", "/k", scriptPath],
            _ => ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", scriptPath],
        };
}
