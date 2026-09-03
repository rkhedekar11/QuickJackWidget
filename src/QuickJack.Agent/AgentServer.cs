using System.Collections.Concurrent;
using System.IO.Pipes;
using QuickJack.Core.Execution;
using QuickJack.Core.Ipc;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Agent;

/// <summary>
/// Which safety checks the agent enforces. Both are on in production; the tests turn them
/// off to exercise the wire protocol, because producing a genuinely Administrators-owned
/// directory or a matching client path needs elevation a test run does not have.
/// </summary>
public sealed record AgentPolicy
{
    public bool RequireSecuredStore { get; init; } = true;
    public bool RequireKnownClient { get; init; } = true;
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

    public async Task RunAsync(CancellationToken ct)
    {
        // An unprotected pinned store is writable by anything running as the user, which
        // would turn this process into a free privilege escalation. Refuse to serve at all.
        if (_policy.RequireSecuredStore && !PinnedStore.IsDirectorySecured(paths))
        {
            log.Error($"{paths.MachineDirectory} is not locked down. Refusing to start.");
            return;
        }

        log.Info("Agent started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ServeOneAsync(ct);
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

        log.Info("Agent stopped.");
    }

    private async Task ServeOneAsync(CancellationToken ct)
    {
        // CurrentUserOnly makes Windows refuse a client running as a different account, so
        // another user on the machine cannot reach this pipe at all.
        using var server = new NamedPipeServerStream(
            PipeProtocol.PipeName(),
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await server.WaitForConnectionAsync(ct);

        if (!IsClientAllowed(server, out var reason))
        {
            log.Error($"Rejected a connection: {reason}");
            await PipeProtocol.WriteAsync(server, new PipeMessage
            {
                Kind = PipeMessageKind.Error,
                Text = "This client is not permitted to use the QuickJack agent.",
            }, CancellationToken.None);
            return;
        }

        while (server.IsConnected && !ct.IsCancellationRequested)
        {
            var message = await PipeProtocol.ReadAsync(server, ct);
            if (message is null) break;

            switch (message.Kind)
            {
                case PipeMessageKind.Run:
                    await HandleRunAsync(server, message, ct);
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

    private async Task HandleRunAsync(Stream stream, PipeMessage request, CancellationToken ct)
    {
        var runId = request.RunId ?? Guid.NewGuid().ToString("N")[..12];

        if (string.IsNullOrWhiteSpace(request.CommandId))
        {
            await FailAsync(stream, runId, "No command id supplied.", ct);
            return;
        }

        // Resolved from the agent's own read of the admin-owned store. The widget sends an
        // id and never script text, so a compromised widget cannot run arbitrary code here.
        var command = PinnedStore.Find(paths, request.CommandId);

        if (command is null)
        {
            log.Error($"Refused '{request.CommandId}': not in the pinned store.");
            await FailAsync(stream, runId, $"'{request.CommandId}' is not a pinned command.", ct);
            return;
        }

        if (command.Elevation != ElevationMode.Agent)
        {
            await FailAsync(stream, runId,
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
            await FailAsync(stream, runId, ex.Message, ct);
            return;
        }

        _active[runId] = handle;

        try
        {
            await foreach (var line in handle.Output.ReadAllAsync(ct))
            {
                await PipeProtocol.WriteAsync(stream, new PipeMessage
                {
                    Kind = PipeMessageKind.Line,
                    RunId = runId,
                    Stream = line.Stream.ToString(),
                    Text = line.Text,
                }, ct);
            }

            var result = await handle.Completion;

            await PipeProtocol.WriteAsync(stream, new PipeMessage
            {
                Kind = PipeMessageKind.Done,
                RunId = runId,
                State = result.State.ToString(),
                ExitCode = result.ExitCode,
                DurationMs = result.Duration.TotalMilliseconds,
            }, ct);

            // The audit trail lives in the admin-owned directory, so a user-level process
            // cannot erase evidence of what it asked the agent to do.
            log.Info($"Finished '{command.Id}' (run {runId}): {result.State}, exit {result.ExitCode}.");
        }
        finally
        {
            _active.TryRemove(runId, out _);
        }
    }

    private static async Task FailAsync(Stream stream, string runId, string message, CancellationToken ct) =>
        await PipeProtocol.WriteAsync(stream, new PipeMessage
        {
            Kind = PipeMessageKind.Done,
            RunId = runId,
            State = RunState.Faulted.ToString(),
            Text = message,
        }, ct);

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
}
