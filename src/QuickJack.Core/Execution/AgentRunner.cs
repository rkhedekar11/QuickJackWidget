using System.IO.Pipes;
using System.Threading.Channels;
using QuickJack.Core.Ipc;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>
/// Sends a pinned command to the elevated agent and streams its output back, with no UAC
/// prompt.
/// <para>
/// Only a command <em>id</em> goes over the pipe. The agent resolves it against the
/// admin-owned pinned store itself, so even a compromised widget cannot use this to run
/// arbitrary elevated code.
/// </para>
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        // Validated here as well as in the agent: a bad value should be reported before a
        // round trip, and the agent must never rely on the client having checked.
        _ = ParameterBinder.Bind(command, arguments);

        var runId = RunHandle.NewRunId();
        var channel = Channel.CreateUnbounded<OutputLine>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var completion = ExecuteAsync(command, arguments, runId, channel.Writer, cts);
        return new RunHandle(runId, command, channel.Reader, completion, cts);
    }

    private static async Task<RunResult> ExecuteAsync(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments,
        string runId,
        ChannelWriter<OutputLine> writer,
        CancellationTokenSource cts)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            // CurrentUserOnly makes Windows verify the server runs as us, which stops a
            // lower-privileged process squatting the pipe name and impersonating the agent.
            using var pipe = new NamedPipeClientStream(
                ".", PipeProtocol.PipeName(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cts.Token);
            }
            catch (TimeoutException)
            {
                return Fault(writer, started,
                    "The QuickJack agent is not running. Reinstall it from settings, or run " +
                    "this command with a UAC prompt instead.");
            }

            await PipeProtocol.WriteAsync(pipe, new PipeMessage
            {
                Kind = PipeMessageKind.Run,
                RunId = runId,
                CommandId = command.Id,
                Arguments = arguments?.ToDictionary(a => a.Key, a => a.Value),
            }, cts.Token);

            // A cancel travels as its own message: only the elevated agent can kill an
            // elevated process, so it has to do it on our behalf.
            using var cancelRegistration = cts.Token.Register(() =>
                _ = SendCancelAsync(pipe, runId));

            return await PumpAsync(pipe, writer, started, cts.Token);
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(OutputLine.Info("Cancelled."));
            return new RunResult(RunState.Cancelled, null,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            return Fault(writer, started, ex.Message);
        }
        finally
        {
            writer.TryComplete();
            cts.Dispose();
        }
    }

    private static async Task<RunResult> PumpAsync(
        Stream pipe, ChannelWriter<OutputLine> writer, long started, CancellationToken ct)
    {
        while (true)
        {
            var message = await PipeProtocol.ReadAsync(pipe, ct);

            if (message is null)
            {
                return Fault(writer, started, "The agent closed the connection unexpectedly.");
            }

            switch (message.Kind)
            {
                case PipeMessageKind.Line:
                    var stream = Enum.TryParse<OutputStream>(message.Stream, out var s)
                        ? s
                        : OutputStream.StdOut;
                    writer.TryWrite(new OutputLine(stream, message.Text ?? string.Empty, DateTimeOffset.Now));
                    break;

                case PipeMessageKind.Done:
                    if (!string.IsNullOrEmpty(message.Text)) writer.TryWrite(OutputLine.Err(message.Text));

                    var state = Enum.TryParse<RunState>(message.State, out var parsed)
                        ? parsed
                        : RunState.Faulted;

                    return new RunResult(
                        state,
                        message.ExitCode,
                        message.DurationMs is { } ms
                            ? TimeSpan.FromMilliseconds(ms)
                            : System.Diagnostics.Stopwatch.GetElapsedTime(started));

                case PipeMessageKind.Error:
                    return Fault(writer, started, message.Text ?? "The agent refused the request.");

                default:
                    return Fault(writer, started, $"Unexpected message '{message.Kind}' from the agent.");
            }
        }
    }

    private static async Task SendCancelAsync(Stream pipe, string runId)
    {
        try
        {
            await PipeProtocol.WriteAsync(pipe, new PipeMessage
            {
                Kind = PipeMessageKind.Cancel,
                RunId = runId,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe is already gone, which means the run is already over.
        }
    }

    private static RunResult Fault(ChannelWriter<OutputLine> writer, long started, string error)
    {
        writer.TryWrite(OutputLine.Err(error));
        return new RunResult(RunState.Faulted, null,
            System.Diagnostics.Stopwatch.GetElapsedTime(started), error);
    }

    /// <summary>True if an agent is currently listening.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeProtocol.PipeName(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.ConnectAsync(300);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
