using System.Collections.Concurrent;
using System.IO.Pipes;
using QuickJack.Core.Execution;
using QuickJack.Core.Ipc;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Agent;

/// <summary>
/// Which safety checks the agent enforces, and where it listens. The checks are both on in
/// production; the tests turn them off to exercise the wire protocol, because producing a
/// genuinely Administrators-owned directory or a matching client path needs elevation a test
/// run does not have.
/// </summary>
public sealed record AgentPolicy
{
    public bool RequireSecuredStore { get; init; } = true;
    public bool RequireKnownClient { get; init; } = true;

    /// <summary>
    /// Pipe to listen on. Tests give each server its own name so they collide neither with
    /// each other nor with a real agent installed on the same machine.
    /// </summary>
    public string PipeName { get; init; } = PipeProtocol.PipeName();
}

/// <summary>
/// The elevated half of QuickJack. Runs as the interactive user with an administrator token
/// (a Scheduled Task with "run with highest privileges"), and executes only commands the
/// admin-owned pinned store already contains.
/// <para>
/// Running as the user rather than SYSTEM is deliberate: the agent can then only do what the
/// user could already do by clicking through UAC, which makes it a UAC bypass for that
/// account rather than a SYSTEM escalation — and per-user tooling on PATH keeps working.
/// </para>
/// </summary>
public sealed class AgentServer(QuickJackPaths paths, AgentLog log, AgentPolicy? policy = null)
{
    private readonly AgentPolicy _policy = policy ?? new AgentPolicy();

    private readonly LocalProcessRunner _runner = new();
    private readonly ConcurrentDictionary<string, RunHandle> _active = new();

    private readonly TaskCompletionSource _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the pipe exists and is accepting. Tests wait on it.</summary>
    internal Task Listening => _listening.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        // An unprotected pinned store is writable by anything running as the user, which
        // would turn this process into a free privilege escalation. Refuse to serve at all.
        if (_policy.RequireSecuredStore && !PinnedStore.IsDirectorySecured(paths))
        {
            log.Error($"{paths.MachineDirectory} is not locked down. Refusing to start.");
            _listening.TrySetException(new InvalidOperationException(
                $"{paths.MachineDirectory} is not locked down."));
            return;
        }

        log.Info("Agent started.");

        var connections = new ConcurrentDictionary<Task, byte>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Served on its own task so the listener is free again immediately: two
                // commands launched together each open their own connection, and making the
                // second wait for the first to finish would look like the agent is down.
                Track(connections, await AcceptAsync(ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Error("Connection failed", ex);
                await Task.Delay(500, CancellationToken.None);
            }
        }

        await Task.WhenAll(connections.Keys).ConfigureAwait(false);
        log.Info("Agent stopped.");
    }

    /// <summary>Waits for one client, and returns the task that serves it.</summary>
    private async Task<Task> AcceptAsync(CancellationToken ct)
    {
        // CurrentUserOnly makes Windows refuse a client running as a different account, so
        // another user on the machine cannot reach this pipe at all.
        var server = new NamedPipeServerStream(
            _policy.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeProtocol.BufferBytes,
            PipeProtocol.BufferBytes);

        _listening.TrySetResult();

        try
        {
            await server.WaitForConnectionAsync(ct);
        }
        catch
        {
            server.Dispose();
            throw;
        }

        return Task.Run(async () =>
        {
            try
            {
                await ServeAsync(server, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.Error("Connection failed", ex);
            }
            finally
            {
                server.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var session = new Session(server);

        if (!IsClientAllowed(server, out var reason))
        {
            log.Error($"Rejected a connection: {reason}");

            try
            {
                await session.WriteAsync(new PipeMessage
                {
                    Kind = PipeMessageKind.Error,
                    Text = "This client is not permitted to use the QuickJack agent.",
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Telling a client that has already hung up is not a server error. An
                // availability probe connects and drops exactly like this.
            }

            return;
        }

        // Runs belong to the connection that asked for them. Once the client is gone there
        // is nothing left to receive their output, and an elevated process nobody can see
        // or stop is exactly what this design must not leave behind.
        using var connectionRuns = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runs = new ConcurrentDictionary<Task, byte>();

        try
        {
            while (server.IsConnected && !ct.IsCancellationRequested)
            {
                var message = await PipeProtocol.ReadAsync(server, ct);
                if (message is null) break;

                switch (message.Kind)
                {
                    case PipeMessageKind.Run:
                        // Deliberately not awaited. The read loop has to keep reading, or a
                        // Cancel sent during a run would only be seen once the run it means
                        // to stop had already finished.
                        Track(runs, RunSafelyAsync(session, message, connectionRuns.Token));
                        break;

                    case PipeMessageKind.Cancel:
                        if (message.RunId is { } runId && _active.TryGetValue(runId, out var handle))
                            handle.Cancel();
                        break;

                    default:
                        // The widget never sends anything else; treat it as a protocol error
                        // rather than guessing at intent.
                        log.Error($"Unexpected message kind '{message.Kind}'.");
                        return;
                }
            }
        }
        finally
        {
            connectionRuns.Cancel();
            await Task.WhenAll(runs.Keys).ConfigureAwait(false);
        }
    }

    private async Task RunSafelyAsync(Session session, PipeMessage request, CancellationToken ct)
    {
        try
        {
            await HandleRunAsync(session, request, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                      or InvalidOperationException)
        {
            // The client vanished mid-run; its process was cancelled along with the
            // connection. Nothing here should be able to take the agent down.
            log.Error($"Run '{request.RunId}' ended without reaching its client: {ex.Message}");
        }
    }

    private async Task HandleRunAsync(Session session, PipeMessage request, CancellationToken ct)
    {
        var runId = request.RunId ?? Guid.NewGuid().ToString("N")[..12];

        if (string.IsNullOrWhiteSpace(request.CommandId))
        {
            await FailAsync(session, runId, "No command id supplied.", ct);
            return;
        }

        // Resolved from the agent's own read of the admin-owned store. The widget sends an
        // id and never script text, so a compromised widget cannot run arbitrary code here.
        var command = PinnedStore.Find(paths, request.CommandId);

        if (command is null)
        {
            log.Error($"Refused '{request.CommandId}': not in the pinned store.");
            await FailAsync(session, runId, $"'{request.CommandId}' is not a pinned command.", ct);
            return;
        }

        if (command.Elevation != ElevationMode.Agent)
        {
            await FailAsync(session, runId,
                $"'{command.Id}' is not marked for no-prompt elevation.", ct);
            return;
        }

        log.Info($"Running '{command.Id}' (run {runId}) with arguments: " +
                 (request.Arguments is { Count: > 0 }
                     ? string.Join(", ", request.Arguments.Select(a => $"{a.Key}={a.Value}"))
                     : "none"));

        RunHandle handle;
        try
        {
            // Elevation comes from this process's own token, so the command itself runs
            // as an ordinary local process from here.
            handle = _runner.Start(command with { Elevation = ElevationMode.None }, request.Arguments, ct);
        }
        catch (ParameterValidationException ex)
        {
            log.Error($"Refused '{command.Id}': {ex.Message}");
            await FailAsync(session, runId, ex.Message, ct);
            return;
        }

        _active[runId] = handle;

        try
        {
            // Drained to completion whatever happens: cancelling stops the child, but its
            // last lines and its real exit state still have to reach the client.
            await foreach (var line in handle.Output.ReadAllAsync(CancellationToken.None))
            {
                await session.WriteAsync(new PipeMessage
                {
                    Kind = PipeMessageKind.Line,
                    RunId = runId,
                    Stream = line.Stream.ToString(),
                    Text = line.Text,
                }, CancellationToken.None);
            }

            var result = await handle.Completion;

            await session.WriteAsync(new PipeMessage
            {
                Kind = PipeMessageKind.Done,
                RunId = runId,
                State = result.State.ToString(),
                ExitCode = result.ExitCode,
                DurationMs = result.Duration.TotalMilliseconds,
            }, CancellationToken.None);

            // The audit trail lives in the admin-owned directory, so a user-level process
            // cannot erase evidence of what it asked the agent to do.
            log.Info($"Finished '{command.Id}' (run {runId}): {result.State}, exit {result.ExitCode}.");
        }
        finally
        {
            _active.TryRemove(runId, out _);
        }
    }

    private static async Task FailAsync(Session session, string runId, string message, CancellationToken ct) =>
        await session.WriteAsync(new PipeMessage
        {
            Kind = PipeMessageKind.Done,
            RunId = runId,
            State = RunState.Faulted.ToString(),
            Text = message,
        }, ct);

    private static void Track(ConcurrentDictionary<Task, byte> tasks, Task task)
    {
        tasks[task] = 0;
        _ = task.ContinueWith(t => tasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Only the executable recorded at install time may connect. The record lives in the
    /// admin-owned directory, so the widget cannot rewrite it to point somewhere else.
    /// </summary>
    private bool IsClientAllowed(NamedPipeServerStream server, out string reason)
    {
        reason = string.Empty;
        if (!_policy.RequireKnownClient) return true;

        var allowed = PinnedStore.ReadAllowedClientPath(paths);
        if (string.IsNullOrWhiteSpace(allowed))
        {
            reason = "no client path is recorded; reinstall the agent";
            return false;
        }

        var actual = ClientProcess.ImagePathOf(server);
        if (actual is null)
        {
            reason = "the client process could not be identified";
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(actual), Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"'{actual}' is not the installed widget";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// One client connection. Concurrent runs share the one pipe, so frames are serialised
    /// through a gate: two interleaved writes would corrupt the length prefixes and
    /// desynchronise the stream permanently.
    /// </summary>
    private sealed class Session(Stream stream)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(PipeMessage message, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PipeProtocol.WriteAsync(stream, message, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
