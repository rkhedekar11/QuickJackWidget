using System.Threading.Channels;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

public enum OutputStream
{
    StdOut,
    StdErr,
    /// <summary>QuickJack's own commentary (e.g. "waiting for elevation"), not the script's.</summary>
    Info,
}

public readonly record struct OutputLine(OutputStream Stream, string Text, DateTimeOffset At)
{
    public static OutputLine Out(string text) => new(OutputStream.StdOut, text, DateTimeOffset.Now);
    public static OutputLine Err(string text) => new(OutputStream.StdErr, text, DateTimeOffset.Now);
    public static OutputLine Info(string text) => new(OutputStream.Info, text, DateTimeOffset.Now);
}

public enum RunState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    /// <summary>Could not start at all — missing interpreter, bad working directory, UAC declined.</summary>
    Faulted,
}

public sealed record RunResult(RunState State, int? ExitCode, TimeSpan Duration, string? Error = null)
{
    public bool IsSuccess => State == RunState.Succeeded;
}

/// <summary>A single execution: live output, a completion task, and a way to stop it.</summary>
public sealed class RunHandle
{
    private readonly CancellationTokenSource _cts;

    internal RunHandle(
        string runId,
        CommandDef command,
        ChannelReader<OutputLine> output,
        Task<RunResult> completion,
        CancellationTokenSource cts)
    {
        RunId = runId;
        Command = command;
        Output = output;
        Completion = completion;
        _cts = cts;
    }

    public string RunId { get; }
    public CommandDef Command { get; }

    /// <summary>Live output. Completes when the run does.</summary>
    public ChannelReader<OutputLine> Output { get; }

    public Task<RunResult> Completion { get; }

    /// <summary>Requests cancellation; the runner kills the whole process tree.</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public static string NewRunId() => Guid.NewGuid().ToString("N")[..12];
}

public interface ICommandRunner
{
    /// <summary>
    /// Starts <paramref name="command"/> and returns immediately. Parameter validation
    /// failures throw <see cref="ParameterValidationException"/> synchronously; everything
    /// else surfaces through <see cref="RunHandle.Completion"/>.
    /// </summary>
    RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default);
}
