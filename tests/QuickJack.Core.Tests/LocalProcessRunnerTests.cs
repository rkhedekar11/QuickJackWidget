using System.Diagnostics;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;

namespace QuickJack.Core.Tests;

/// <summary>
/// These start real interpreters. They are the only place the shell plumbing — encoding,
/// exit codes, process-tree teardown — is actually proven.
/// </summary>
public class LocalProcessRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CommandDef Command(
        string script,
        ShellKind shell = ShellKind.Cmd,
        int timeoutSeconds = 30,
        params ParameterDef[] parameters) => new()
    {
        Id = "test",
        Name = "Test",
        Shell = shell,
        Script = script,
        TimeoutSeconds = timeoutSeconds,
        Parameters = parameters,
    };

    private static async Task<(RunResult Result, List<OutputLine> Lines)> RunAsync(
        CommandDef command, IReadOnlyDictionary<string, string>? args = null)
    {
        var handle = new LocalProcessRunner().Start(command, args);
        var lines = await CollectAsync(handle);
        var result = await handle.Completion.WaitAsync(Patience);
        return (result, lines);
    }

    private static async Task<List<OutputLine>> CollectAsync(RunHandle handle)
    {
        var lines = new List<OutputLine>();
        await foreach (var line in handle.Output.ReadAllAsync().WithCancellation(default))
            lines.Add(line);
        return lines;
    }

    private static string TextOf(IEnumerable<OutputLine> lines, OutputStream stream) =>
        string.Join("\n", lines.Where(l => l.Stream == stream).Select(l => l.Text)).Trim();

    // ---- basics ----

    [Fact]
    public async Task Cmd_command_streams_stdout_and_succeeds()
    {
        var (result, lines) = await RunAsync(Command("echo hello"));

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", TextOf(lines, OutputStream.StdOut));
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task PowerShell_command_streams_stdout_and_succeeds()
    {
        var (result, lines) = await RunAsync(Command("Write-Output 'hello'", ShellKind.PowerShell));

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("hello", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task A_nonzero_exit_code_is_reported_as_Failed()
    {
        var (result, _) = await RunAsync(Command("exit /b 3"));

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task PowerShell_propagates_its_exit_code()
    {
        var (result, _) = await RunAsync(Command("exit 7", ShellKind.PowerShell));

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task Stderr_is_captured_separately_from_stdout()
    {
        var (result, lines) = await RunAsync(Command("echo to-out& echo to-err 1>&2"));

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("to-out", TextOf(lines, OutputStream.StdOut));
        Assert.Equal("to-err", TextOf(lines, OutputStream.StdErr));
    }

    [Fact]
    public async Task Multi_line_scripts_run_in_order()
    {
        var (_, lines) = await RunAsync(Command("echo one\necho two\necho three"));

        Assert.Equal("one\ntwo\nthree", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Non_ascii_output_survives_the_encoding_round_trip()
    {
        // The interpreters default to an OEM code page; the script preamble is what makes
        // this come back intact.
        var (_, lines) = await RunAsync(Command("Write-Output 'héllo — 日本語'", ShellKind.PowerShell));

        Assert.Equal("héllo — 日本語", TextOf(lines, OutputStream.StdOut));
    }

    // ---- parameters ----

    [Fact]
    public async Task Parameters_reach_a_cmd_script_via_delayed_expansion()
    {
        var (_, lines) = await RunAsync(
            Command("echo !QJ_HOST!", parameters: new ParameterDef { Name = "host" }),
            new Dictionary<string, string> { ["host"] = "example.com" });

        Assert.Equal("example.com", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Parameters_reach_a_PowerShell_script_as_environment_variables()
    {
        var (_, lines) = await RunAsync(
            Command("Write-Output $env:QJ_HOST", ShellKind.PowerShell,
                parameters: new ParameterDef { Name = "host" }),
            new Dictionary<string, string> { ["host"] = "example.com" });

        Assert.Equal("example.com", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Metacharacters_are_inert_under_cmd_delayed_expansion()
    {
        // The payload must be echoed verbatim, not executed. Delayed expansion happens
        // after the line is parsed, so '&' never becomes a command separator.
        var (_, lines) = await RunAsync(
            Command("echo !QJ_ARG!", parameters: new ParameterDef { Name = "arg" }),
            new Dictionary<string, string> { ["arg"] = "safe & echo pwned" });

        Assert.Equal("safe & echo pwned", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Metacharacters_are_inert_in_PowerShell_environment_reads()
    {
        var (_, lines) = await RunAsync(
            Command("Write-Output $env:QJ_ARG", ShellKind.PowerShell,
                parameters: new ParameterDef { Name = "arg" }),
            new Dictionary<string, string> { ["arg"] = "safe; Write-Output pwned" });

        Assert.Equal("safe; Write-Output pwned", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public void A_cmd_script_using_percent_expansion_is_refused()
    {
        // cmd expands %VAR% before parsing the line, so metacharacters in the value WOULD
        // execute. The unsafe form is rejected outright rather than silently allowed.
        var command = Command("echo %QJ_ARG%", parameters: new ParameterDef { Name = "arg" });

        var ex = Assert.Throws<ParameterValidationException>(
            () => new LocalProcessRunner().Start(
                command, new Dictionary<string, string> { ["arg"] = "x" }));

        Assert.Contains("!QJ_ARG!", ex.Message);
    }

    [Fact]
    public void Invalid_parameters_throw_synchronously_rather_than_starting_a_process()
    {
        var command = Command("echo x", parameters: new ParameterDef { Name = "host", Required = true });

        Assert.Throws<ParameterValidationException>(() => new LocalProcessRunner().Start(command));
    }

    // ---- lifecycle ----

    [Fact]
    public async Task A_command_that_exceeds_its_timeout_is_killed()
    {
        var started = Stopwatch.GetTimestamp();
        var (result, _) = await RunAsync(Command("ping -n 60 127.0.0.1 >nul", timeoutSeconds: 2));

        Assert.Equal(RunState.TimedOut, result.State);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20),
            "the timeout should stop the run promptly, not wait for the script");
    }

    [Fact]
    public async Task Cancelling_stops_a_running_command()
    {
        var handle = new LocalProcessRunner().Start(Command("ping -n 60 127.0.0.1 >nul"));

        handle.Cancel();
        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Cancelled, result.State);
    }

    [Fact]
    public async Task Cancelling_kills_the_whole_process_tree()
    {
        // The interpreter is not the thing doing the work; killing only it would orphan
        // the child and leave it running after the widget says "cancelled".
        var marker = Path.Combine(Path.GetTempPath(), $"qj-tree-{Guid.NewGuid():N}.txt");
        var script = $"""
            start /b cmd /c "ping -n 30 127.0.0.1 >nul & echo survived > {marker}"
            ping -n 30 127.0.0.1 >nul
            """;

        var handle = new LocalProcessRunner().Start(Command(script));
        await Task.Delay(1500);
        handle.Cancel();
        await handle.Completion.WaitAsync(Patience);

        await Task.Delay(1000);
        Assert.False(File.Exists(marker), "a grandchild process outlived the cancellation");
    }

    [Fact]
    public async Task The_output_channel_completes_when_the_run_does()
    {
        var handle = new LocalProcessRunner().Start(Command("echo done"));

        await CollectAsync(handle); // returns only once the channel completes
        Assert.True(handle.Completion.IsCompleted || await handle.Completion.WaitAsync(Patience) is not null);
    }

    [Fact]
    public async Task A_missing_working_directory_is_ignored_rather_than_faulting()
    {
        var command = Command("echo ok") with { WorkingDirectory = @"Z:\does\not\exist" };
        var (result, lines) = await RunAsync(command);

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("ok", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Working_directory_is_honoured_when_it_exists()
    {
        using var root = new TempRoot();
        var command = Command("cd") with { WorkingDirectory = root.Paths.UserDirectory };

        var (_, lines) = await RunAsync(command);

        Assert.Equal(
            Path.GetFullPath(root.Paths.UserDirectory).TrimEnd('\\'),
            TextOf(lines, OutputStream.StdOut).TrimEnd('\\'));
    }

    [Fact]
    public async Task A_script_that_reads_stdin_does_not_hang()
    {
        // stdin is redirected and closed, so a stray prompt sees EOF instead of blocking
        // forever against a console nobody is attached to.
        var (result, _) = await RunAsync(
            Command("$x = Read-Host 'name'; Write-Output \"got:$x\"", ShellKind.PowerShell, timeoutSeconds: 15));

        Assert.NotEqual(RunState.TimedOut, result.State);
    }

    [Fact]
    public async Task Temp_scripts_do_not_accumulate()
    {
        var before = Directory.Exists(ScriptWriter.Directory)
            ? Directory.GetFiles(ScriptWriter.Directory).Length
            : 0;

        for (var i = 0; i < 3; i++) await RunAsync(Command("echo x"));

        var after = Directory.GetFiles(ScriptWriter.Directory).Length;
        Assert.True(after <= before, $"temp scripts leaked: {before} -> {after}");
    }
}
