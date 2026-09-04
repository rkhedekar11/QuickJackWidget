using QuickJack.Agent;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

/// <summary>
/// The elevated agent, driven over a real named pipe by the real client. Everything is in
/// one test process, so nothing here is genuinely elevated — the agent's checks that need
/// administrator (an Administrators-owned store directory, the installed widget's path) are
/// switched off through <see cref="AgentPolicy"/> and stay on the manual checklist. What is
/// covered is the part that decides <em>what runs</em>, which is where the escalation risk
/// actually lives.
/// </summary>
public class AgentServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CommandDef Pinned(
        string id,
        string script,
        ElevationMode elevation = ElevationMode.Agent,
        params ParameterDef[] parameters) => new()
    {
        Id = id,
        Name = id,
        Shell = ShellKind.Cmd,
        Script = script,
        Elevation = elevation,
        Origin = CommandOrigin.Pinned,
        TimeoutSeconds = 30,
        Parameters = parameters,
    };

    private static async Task<List<OutputLine>> CollectAsync(RunHandle handle)
    {
        var lines = new List<OutputLine>();
        await foreach (var line in handle.Output.ReadAllAsync())
            lines.Add(line);
        return lines;
    }

    private static string TextOf(IEnumerable<OutputLine> lines, OutputStream stream) =>
        string.Join("\n", lines.Where(l => l.Stream == stream).Select(l => l.Text)).Trim();

    private static string AllText(IEnumerable<OutputLine> lines) =>
        string.Join("\n", lines.Select(l => l.Text));

    /// <summary>An agent listening on a pipe of its own, over an isolated state root.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private Task _serving = Task.CompletedTask;

        public TempRoot Root { get; } = new();

        // Unique per harness, so tests neither collide with each other nor with a real
        // agent installed on the machine running them.
        public string PipeName { get; } = $"QuickJack.Tests.{Guid.NewGuid():N}";

        public AgentServer Server { get; private set; } = null!;

        public AgentRunner Client => new(PipeName);

        public Task Start(bool requireSecuredStore = false, bool requireKnownClient = false)
        {
            Server = new AgentServer(Root.Paths, new AgentLog(Root.Paths), new AgentPolicy
            {
                RequireSecuredStore = requireSecuredStore,
                RequireKnownClient = requireKnownClient,
                PipeName = PipeName,
            });

            return _serving = Server.RunAsync(_stopping.Token);
        }

        public async Task<Harness> StartedAsync(bool requireKnownClient = false)
        {
            // Runs for the lifetime of the harness; awaited in DisposeAsync.
            _ = Start(requireKnownClient: requireKnownClient);
            await Server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
            return this;
        }

        public void Pin(params CommandDef[] commands) => PinnedStore.Write(Root.Paths, commands);

        public void RecordClient(string path) =>
            File.WriteAllText(PinnedStore.ClientPathFile(Root.Paths), path);

        public string Log() =>
            File.Exists(Root.Paths.AgentLog) ? File.ReadAllText(Root.Paths.AgentLog) : string.Empty;

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            try { await _serving.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException
                                          or InvalidOperationException) { }

            _stopping.Dispose();
            Root.Dispose();
        }
    }

    // ---- what the agent will and will not run ----

    [Fact]
    public async Task A_pinned_agent_command_runs_and_streams_its_output()
    {
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("hello", "echo hello from the agent"));

        var handle = agent.Client.Start(Pinned("hello", "echo hello from the agent"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello from the agent", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task The_agent_runs_the_pinned_script_not_the_one_the_client_sent()
    {
        // The invariant the whole design rests on: only an id crosses the pipe, and the
        // agent resolves it against the admin-owned store itself. A widget that has been
        // compromised cannot use the agent as an arbitrary elevated shell.
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("job", "echo the-pinned-script"));

        var handle = agent.Client.Start(Pinned("job", "echo the-substituted-script"));
        var lines = await CollectAsync(handle);
        await handle.Completion.WaitAsync(Patience);

        Assert.Equal("the-pinned-script", TextOf(lines, OutputStream.StdOut));
        Assert.DoesNotContain("substituted", AllText(lines));
    }

    [Fact]
    public async Task An_id_that_is_not_pinned_is_refused()
    {
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("known", "echo hi"));

        var handle = agent.Client.Start(Pinned("unknown", "echo hi"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.Contains("not a pinned command", AllText(lines));
        Assert.Contains("Refused 'unknown'", agent.Log());
    }

    [Fact]
    public async Task A_pinned_command_that_is_not_marked_for_no_prompt_elevation_is_refused()
    {
        // Being in the pinned store is not on its own permission to run without a prompt.
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("prompted", "echo hi", ElevationMode.Uac));

        var handle = agent.Client.Start(Pinned("prompted", "echo hi"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.Contains("not marked for no-prompt elevation", AllText(lines));
    }

    [Fact]
    public async Task An_empty_pinned_store_means_the_agent_runs_nothing()
    {
        await using var agent = await new Harness().StartedAsync();

        var handle = agent.Client.Start(Pinned("anything", "echo hi"));
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
    }

    [Fact]
    public async Task A_failing_command_reports_its_exit_code()
    {
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("failing", "exit /b 3"));

        var handle = agent.Client.Start(Pinned("failing", "exit /b 3"));
        await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(3, result.ExitCode);
    }

    // ---- parameters ----

    [Fact]
    public async Task Arguments_reach_the_command_as_environment_variables()
    {
        var command = Pinned("greet", "echo hello !QJ_WHO!", ElevationMode.Agent,
            new ParameterDef { Name = "who", Required = true });

        await using var agent = await new Harness().StartedAsync();
        agent.Pin(command);

        var handle = agent.Client.Start(command, new Dictionary<string, string> { ["who"] = "world" });
        var lines = await CollectAsync(handle);
        await handle.Completion.WaitAsync(Patience);

        Assert.Equal("hello world", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task A_value_the_pinned_definition_rejects_never_runs()
    {
        // The client validates against its own copy, but the agent must not depend on that
        // having happened: it re-validates against the definition it read itself.
        var pinnedDefinition = Pinned("digits", "echo !QJ_N!", ElevationMode.Agent,
            new ParameterDef { Name = "n", Required = true, Pattern = "[0-9]+" });

        await using var agent = await new Harness().StartedAsync();
        agent.Pin(pinnedDefinition);

        // A client whose local copy declares no pattern, so nothing stops the value here.
        var clientCopy = Pinned("digits", "echo !QJ_N!", ElevationMode.Agent,
            new ParameterDef { Name = "n" });

        var handle = agent.Client.Start(clientCopy, new Dictionary<string, string> { ["n"] = "not-a-number" });
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.DoesNotContain("not-a-number", TextOf(lines, OutputStream.StdOut));
    }

    // ---- cancelling ----

    [Fact]
    public async Task Cancelling_stops_the_run_and_reports_it()
    {
        // Only the elevated agent can kill an elevated process, so cancellation has to make
        // the round trip: the client asks, the agent kills, the client waits to hear back.
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("slow", "echo started\r\nping -n 60 127.0.0.1 > nul"));

        var handle = agent.Client.Start(Pinned("slow", "echo started\r\nping -n 60 127.0.0.1 > nul"));

        // Wait until it is genuinely running, so this is not a race with process startup.
        var lines = await ReadUntilAsync(handle, "started");

        handle.Cancel();

        lines.AddRange(await CollectAsync(handle));
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Cancelled, result.State);

        // Said only when the agent never confirmed — i.e. when an elevated process may have
        // been left running with nothing able to stop it.
        Assert.DoesNotContain("may still be running", AllText(lines));
        Assert.Contains("Finished 'slow'", agent.Log());
    }

    /// <summary>Reads output until a line contains <paramref name="marker"/>.</summary>
    private static async Task<List<OutputLine>> ReadUntilAsync(RunHandle handle, string marker)
    {
        var lines = new List<OutputLine>();
        using var timeout = new CancellationTokenSource(Patience);

        await foreach (var line in handle.Output.ReadAllAsync(timeout.Token))
        {
            lines.Add(line);
            if (line.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return lines;
        }

        Assert.Fail($"The run ended without ever printing '{marker}'.");
        return lines;
    }

    // ---- concurrency ----

    [Fact]
    public async Task Two_commands_can_run_at_the_same_time()
    {
        // Each run opens its own connection. Serving them one at a time would make a second
        // command look like "the agent is not running" until the first had finished.
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(
            Pinned("slow", "echo started\r\nping -n 60 127.0.0.1 > nul"),
            Pinned("quick", "echo quick"));

        var slow = agent.Client.Start(Pinned("slow", string.Empty));
        await ReadUntilAsync(slow, "started");

        var quick = agent.Client.Start(Pinned("quick", string.Empty));
        var quickLines = await CollectAsync(quick);

        Assert.Equal(RunState.Succeeded, (await quick.Completion.WaitAsync(Patience)).State);
        Assert.Equal("quick", TextOf(quickLines, OutputStream.StdOut));
        Assert.False(slow.Completion.IsCompleted, "The slow run should still be going.");

        slow.Cancel();
        await CollectAsync(slow);
        await slow.Completion.WaitAsync(Patience);
    }

    // ---- refusing to serve ----

    [Fact]
    public async Task The_agent_refuses_to_start_when_the_pinned_store_is_not_locked_down()
    {
        // An unprotected store is writable by anything running as the user, which would make
        // the agent a free privilege escalation. It must not listen at all.
        await using var agent = new Harness();

        await agent.Start(requireSecuredStore: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("Refusing to start", agent.Log());
        Assert.False(await AgentRunner.IsAvailableAsync(agent.PipeName));
    }

    [Fact]
    public async Task A_client_with_no_recorded_path_is_rejected()
    {
        await using var agent = await new Harness().StartedAsync(requireKnownClient: true);
        agent.Pin(Pinned("hello", "echo hello"));

        var handle = agent.Client.Start(Pinned("hello", "echo hello"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.Contains("not permitted", AllText(lines));
        Assert.Contains("no client path is recorded", agent.Log());
    }

    [Fact]
    public async Task A_client_that_is_not_the_installed_widget_is_rejected()
    {
        await using var agent = await new Harness().StartedAsync(requireKnownClient: true);
        agent.Pin(Pinned("hello", "echo hello"));
        agent.RecordClient(@"C:\Windows\System32\notepad.exe");

        var handle = agent.Client.Start(Pinned("hello", "echo hello"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.Contains("not permitted", AllText(lines));
        Assert.Contains("is not the installed widget", agent.Log());
    }

    [Fact]
    public async Task The_client_recorded_at_install_time_is_accepted()
    {
        // Proves the pipe really does identify the process on the other end — this test
        // process stands in for the installed widget.
        await using var agent = await new Harness().StartedAsync(requireKnownClient: true);
        agent.Pin(Pinned("hello", "echo hello"));
        agent.RecordClient(Environment.ProcessPath!);

        var handle = agent.Client.Start(Pinned("hello", "echo hello"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("hello", TextOf(lines, OutputStream.StdOut));
    }

    // ---- audit trail ----

    [Fact]
    public async Task Every_run_is_recorded_in_the_admin_owned_log()
    {
        // The log lives in the admin-owned directory precisely so a user-level process
        // cannot erase evidence of what it asked the agent to do.
        await using var agent = await new Harness().StartedAsync();
        agent.Pin(Pinned("audited", "echo hi"));

        var handle = agent.Client.Start(Pinned("audited", "echo hi"));
        await CollectAsync(handle);
        await handle.Completion.WaitAsync(Patience);

        var log = agent.Log();
        Assert.Contains("Running 'audited'", log);
        Assert.Contains("Finished 'audited'", log);
    }

    [Fact]
    public async Task Argument_values_are_recorded_with_the_run()
    {
        await using var agent = await new Harness().StartedAsync();
        var command = Pinned("audited", "echo !QJ_WHO!", ElevationMode.Agent,
            new ParameterDef { Name = "who" });
        agent.Pin(command);

        var handle = agent.Client.Start(command, new Dictionary<string, string> { ["who"] = "world" });
        await CollectAsync(handle);
        await handle.Completion.WaitAsync(Patience);

        Assert.Contains("who=world", agent.Log());
    }

    // ---- when no agent is there ----

    [Fact]
    public async Task A_missing_agent_is_reported_as_such_rather_than_hanging()
    {
        var runner = new AgentRunner($"QuickJack.Tests.Absent.{Guid.NewGuid():N}");

        var handle = runner.Start(Pinned("hello", "echo hello"));
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Faulted, result.State);
        Assert.Contains("agent is not running", AllText(lines));
    }
}
