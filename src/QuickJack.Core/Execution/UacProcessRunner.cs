using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Core.Execution;

/// <summary>
/// Runs a command elevated, one UAC consent per run.
/// <para>
/// <b>The constraint that shapes all of this:</b> <c>Verb = "runas"</c> requires
/// <c>UseShellExecute = true</c>, which forbids stream redirection. They are mutually
/// exclusive in Win32, so an elevated child's stdout simply cannot be captured directly.
/// </para>
/// <para>
/// Instead we elevate a small wrapper script. The wrapper starts the real script as its own
/// child <em>with</em> redirection — to files — and this (non-elevated) process tails those
/// files. The wrapper also polls for a cancel sentinel, because a medium-integrity process
/// cannot kill a high-integrity one; only the elevated wrapper can stop its own child.
/// </para>
/// </summary>
public sealed class UacProcessRunner : ICommandRunner
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — the user dismissed the UAC dialog
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    private readonly bool _elevate;

    public UacProcessRunner() : this(elevate: true) { }

    /// <summary>
    /// <paramref name="elevate"/> is a test seam. With it false the wrapper is launched
    /// without the runas verb, which exercises every part of this class — wrapper
    /// generation, output tailing, cancellation, exit-code recovery — without needing a
    /// human to click a UAC dialog. Only the verb differs from the real path.
    /// </summary>
    internal UacProcessRunner(bool elevate) => _elevate = elevate;

    public RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var bound = ParameterBinder.Bind(command, arguments);

        var runId = RunHandle.NewRunId();
        var channel = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = ExecuteAsync(command, bound, runId, channel.Writer, cts, cancellationToken, _elevate);

        return new RunHandle(runId, command, channel.Reader, completion, cts);
    }

    private static async Task<RunResult> ExecuteAsync(
        CommandDef command,
        BoundScript bound,
        string runId,
        ChannelWriter<OutputLine> writer,
        CancellationTokenSource cts,
        CancellationToken externalToken,
        bool elevate)
    {
        var started = Stopwatch.GetTimestamp();
        ElevatedRunFiles? files = null;
        var timedOut = false;

        try
        {
            var executable = ShellInfo.Resolve(command.Shell);
            if (executable is null)
            {
                return Fault(writer, started,
                    $"{ShellInfo.Executable(command.Shell)} was not found on this machine.");
            }

            files = ElevatedRunFiles.Create(runId);
            files.WriteScript(command.Shell, bound.Script);
            files.WriteWrapper(command, bound, executable);

            writer.TryWrite(OutputLine.Info("Waiting for administrator consent..."));

            Process elevated;
            try
            {
                elevated = StartWrapper(files.WrapperPath, elevate);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                // Declining the prompt is a decision, not a failure. Reporting it as an
                // error would train the user to ignore real ones.
                writer.TryWrite(OutputLine.Info("Cancelled: administrator consent was declined."));
                return new RunResult(RunState.Cancelled, null, Stopwatch.GetElapsedTime(started));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return Fault(writer, started, ex.Message);
            }

            using (elevated)
            {
                writer.TryWrite(OutputLine.Info(elevate ? "Running elevated." : "Running."));

                if (command.TimeoutSeconds > 0)
                {
                    var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds));
                    timeout.Token.Register(() => { timedOut = true; cts.Cancel(); });
                }

                await TailAsync(files, elevated, writer, cts.Token).ConfigureAwait(false);

                var elapsed = Stopwatch.GetElapsedTime(started);
                var exitCode = files.ReadExitCode() ?? SafeExitCode(elevated);

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
        }
        catch (Exception ex)
        {
            return Fault(writer, started, ex.Message);
        }
        finally
        {
            files?.Dispose();
            writer.TryComplete();
            cts.Dispose();
        }
    }

    private static Process StartWrapper(string wrapperPath, bool elevate)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellInfo.Resolve(ShellKind.PowerShell) ?? "powershell.exe",
            // runas is the whole point, and it forces UseShellExecute — hence no redirection.
            Verb = elevate ? "runas" : string.Empty,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        foreach (var arg in ShellInfo.Arguments(ShellKind.PowerShell, wrapperPath))
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)
               ?? throw new InvalidOperationException("Failed to start the elevated process.");
    }

    /// <summary>
    /// Follows the redirected output files until the elevated wrapper exits, then drains
    /// whatever it wrote last.
    /// </summary>
    private static async Task TailAsync(
        ElevatedRunFiles files, Process elevated, ChannelWriter<OutputLine> writer, CancellationToken token)
    {
        var stdout = new FileTail(files.StdOutPath, OutputStream.StdOut);
        var stderr = new FileTail(files.StdErrPath, OutputStream.StdErr);

        while (true)
        {
            if (token.IsCancellationRequested && !files.CancelRequested)
            {
                // We cannot Kill an elevated child from here; the wrapper is watching for
                // this file and stops its own child on our behalf.
                files.RequestCancel();
            }

            stdout.Pump(writer);
            stderr.Pump(writer);

            if (elevated.HasExited) break;

            try { await Task.Delay(PollInterval, CancellationToken.None).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }

        // The wrapper may still be flushing as it exits.
        await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
        stdout.Pump(writer);
        stderr.Pump(writer);
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private static RunResult Fault(ChannelWriter<OutputLine> writer, long started, string error)
    {
        writer.TryWrite(OutputLine.Err(error));
        return new RunResult(RunState.Faulted, null, Stopwatch.GetElapsedTime(started), error);
    }
}

/// <summary>Reads newly appended complete lines from a file that another process is writing.</summary>
internal sealed class FileTail(string path, OutputStream stream)
{
    private long _position;
    private string _partial = string.Empty;

    public void Pump(ChannelWriter<OutputLine> writer)
    {
        if (!File.Exists(path)) return;

        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length <= _position) return;
            fs.Seek(_position, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var text = _partial + reader.ReadToEnd();
            _position = fs.Position;

            var lines = text.Split('\n');

            // The last element is whatever comes after the final newline: either an
            // incomplete line still being written, or "". Hold it until the rest arrives.
            _partial = lines[^1];
            for (var i = 0; i < lines.Length - 1; i++)
                writer.TryWrite(new OutputLine(stream, lines[i].TrimEnd('\r'), DateTimeOffset.Now));
        }
        catch (IOException) { /* mid-write; the next poll picks it up */ }
        catch (UnauthorizedAccessException) { }
    }
}
