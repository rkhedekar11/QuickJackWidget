using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>
/// Runs a command as the current process's token — no elevation, no prompt — streaming
/// stdout and stderr back live.
/// </summary>
public sealed class LocalProcessRunner : ICommandRunner
{
    public RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        // Thrown synchronously: a validation failure is the caller's bug, not a run outcome.
        var bound = ParameterBinder.Bind(command, arguments);

        var runId = RunHandle.NewRunId();
        var channel = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = ExecuteAsync(command, bound, runId, channel.Writer, cts, cancellationToken);

        return new RunHandle(runId, command, channel.Reader, completion, cts);
    }

    private static async Task<RunResult> ExecuteAsync(
        CommandDef command,
        BoundScript bound,
        string runId,
        ChannelWriter<OutputLine> writer,
        CancellationTokenSource cts,
        CancellationToken externalToken)
    {
        var started = Stopwatch.GetTimestamp();
        var timedOut = false;
        TempScript? script = null;

        try
        {
            var executable = ShellInfo.Resolve(command.Shell);
            if (executable is null)
            {
                return Fault(writer, started,
                    $"{ShellInfo.Executable(command.Shell)} was not found on this machine.");
            }

            script = ScriptWriter.Write(command.Shell, bound.Script, runId);

            using var process = new Process
            {
                StartInfo = BuildStartInfo(command, bound, executable, script.Path),
                EnableRaisingEvents = true,
            };

            var drained = AttachOutput(process, writer);

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return Fault(writer, started, ex.Message);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (command.TimeoutSeconds > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds));
                timeout.Token.Register(() => { timedOut = true; cts.Cancel(); });
                await WaitAsync(process, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await WaitAsync(process, cts.Token).ConfigureAwait(false);
            }

            // WaitForExitAsync returns once the process is gone, but the redirected readers
            // may still have buffered lines in flight.
            await drained.ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(started);
            var exitCode = process.ExitCode;

            if (timedOut)
            {
                writer.TryWrite(OutputLine.Info($"Timed out after {command.TimeoutSeconds}s."));
                return new RunResult(RunState.TimedOut, exitCode, elapsed);
            }

            if (externalToken.IsCancellationRequested || cts.IsCancellationRequested)
            {
                writer.TryWrite(OutputLine.Info("Cancelled."));
                return new RunResult(RunState.Cancelled, exitCode, elapsed);
            }

            return new RunResult(
                exitCode == 0 ? RunState.Succeeded : RunState.Failed, exitCode, elapsed);
        }
        catch (Exception ex)
        {
            return Fault(writer, started, ex.Message);
        }
        finally
        {
            script?.Dispose();
            writer.TryComplete();
            cts.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        CommandDef command, BoundScript bound, string executable, string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Nothing is attached to type into; leaving stdin open lets a stray prompt hang.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList, never a concatenated string: the runtime handles quoting correctly
        // and a path with spaces or quotes cannot alter the argument boundaries.
        foreach (var arg in ShellInfo.Arguments(command.Shell, scriptPath))
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory) &&
            Directory.Exists(command.WorkingDirectory))
        {
            psi.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var (key, value) in bound.Environment)
            psi.Environment[key] = value;

        psi.Environment["QUICKJACK"] = "1";
        return psi;
    }

    private static Task AttachOutput(Process process, ChannelWriter<OutputLine> writer)
    {
        var stdout = new TaskCompletionSource();
        var stderr = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdout.TrySetResult();
            else writer.TryWrite(OutputLine.Out(e.Data));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderr.TrySetResult();
            else writer.TryWrite(OutputLine.Err(e.Data));
        };

        return Task.WhenAll(stdout.Task, stderr.Task);
    }

    private static async Task WaitAsync(Process process, CancellationToken token)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException) { }

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree: killing only the interpreter would orphan whatever it
            // spawned, which for a script is usually the thing actually doing the work.
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static RunResult Fault(ChannelWriter<OutputLine> writer, long started, string error)
    {
        writer.TryWrite(OutputLine.Err(error));
        return new RunResult(RunState.Faulted, null, Stopwatch.GetElapsedTime(started), error);
    }
}
