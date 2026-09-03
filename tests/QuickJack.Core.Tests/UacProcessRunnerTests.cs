using System.Diagnostics;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;

namespace QuickJack.Core.Tests;

/// <summary>
/// Exercises the elevated path with elevation switched off, so the wrapper generation,
/// file tailing, cancel sentinel and exit-code recovery are all covered without a human
/// clicking a UAC dialog. Only the ShellExecute verb differs from the real thing — the
/// UAC-declined and genuinely-elevated cases stay on the manual checklist in TODO.md.
/// </summary>
public class UacProcessRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CommandDef Command(
        string script,
        ShellKind shell = ShellKind.Cmd,
        int timeoutSeconds = 45,
        params ParameterDef[] parameters) => new()
    {
        Id = "test",
        Name = "Test",
        Shell = shell,
        Script = script,
        Elevation = ElevationMode.Uac,
        TimeoutSeconds = timeoutSeconds,
        Parameters = parameters,
    };

    private static async Task<(RunResult Result, List<OutputLine> Lines)> RunAsync(
        CommandDef command, IReadOnlyDictionary<string, string>? args = null)
    {
        var handle = new UacProcessRunner(elevate: false).Start(command, args);
        var lines = new List<OutputLine>();
        await foreach (var line in handle.Output.ReadAllAsync())
            lines.Add(line);

        return (await handle.Completion.WaitAsync(Patience), lines);
    }

    private static string TextOf(IEnumerable<OutputLine> lines, OutputStream stream) =>
        string.Join("\n", lines.Where(l => l.Stream == stream).Select(l => l.Text)).Trim();

    [Fact]
    public async Task Output_is_recovered_even_though_redirection_is_impossible()
    {
        // The whole reason this class exists: runas forces UseShellExecute, which forbids
        // stream redirection. Output comes back via the wrapper's redirected files.
        var (result, lines) = await RunAsync(Command("echo hello from the wrapper"));

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello from the wrapper", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Stdout_and_stderr_stay_separate()
    {
        var (_, lines) = await RunAsync(Command("echo to-out& echo to-err 1>&2"));

        Assert.Equal("to-out", TextOf(lines, OutputStream.StdOut));
        Assert.Equal("to-err", TextOf(lines, OutputStream.StdErr));
    }

    [Fact]
    public async Task Exit_codes_survive_the_wrapper()
    {
        var (result, _) = await RunAsync(Command("exit /b 42"));

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task PowerShell_commands_work_through_the_wrapper()
    {
        var (result, lines) = await RunAsync(Command("Write-Output 'ps ok'; exit 5", ShellKind.PowerShell));

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("ps ok", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Multiple_lines_arrive_in_order()
    {
        var (_, lines) = await RunAsync(Command("echo one\necho two\necho three"));

        Assert.Equal("one\ntwo\nthree", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Parameters_reach_the_elevated_script()
    {
        // Values travel as JSON data the wrapper reads, never interpolated into its source.
        var (_, lines) = await RunAsync(
            Command("echo !QJ_HOST!", parameters: new ParameterDef { Name = "host" }),
            new Dictionary<string, string> { ["host"] = "example.com" });

        Assert.Equal("example.com", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task A_quote_heavy_parameter_cannot_break_out_of_the_wrapper()
    {
        // If the value were spliced into the generated PowerShell, this would terminate the
        // string and run its own statement.
        var payload = "'; Write-Output 'PWNED'; '";

        var (result, lines) = await RunAsync(
            Command("Write-Output $env:QJ_ARG", ShellKind.PowerShell,
                parameters: new ParameterDef { Name = "arg" }),
            new Dictionary<string, string> { ["arg"] = payload });

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal(payload, TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task A_quote_heavy_working_directory_cannot_break_out_of_the_wrapper()
    {
        // WorkingDirectory is settable through the API, so it is attacker-influenced text
        // that ends up in generated PowerShell source.
        var command = Command("echo ok") with { WorkingDirectory = "C:\\'; Write-Output 'PWNED'; '" };

        var (result, lines) = await RunAsync(command);

        Assert.Equal(RunState.Succeeded, result.State);
        Assert.DoesNotContain("PWNED", TextOf(lines, OutputStream.StdOut));
    }

    [Fact]
    public async Task Cancelling_stops_the_run_via_the_sentinel()
    {
        var handle = new UacProcessRunner(elevate: false)
            .Start(Command("ping -n 60 127.0.0.1 >nul"));

        // Give the wrapper time to start its child before asking it to stop.
        await Task.Delay(2500);
        var started = Stopwatch.GetTimestamp();
        handle.Cancel();

        var result = await handle.Completion.WaitAsync(Patience);

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(25),
            "the cancel sentinel should be noticed promptly");
    }

    [Fact]
    public async Task A_command_that_exceeds_its_timeout_is_stopped()
    {
        var (result, _) = await RunAsync(Command("ping -n 60 127.0.0.1 >nul", timeoutSeconds: 4));

        Assert.Equal(RunState.TimedOut, result.State);
    }

    [Fact]
    public async Task The_run_directory_is_cleaned_up()
    {
        var before = Directory.Exists(ScriptWriter.Directory)
            ? Directory.GetDirectories(ScriptWriter.Directory, "run-*").Length
            : 0;

        await RunAsync(Command("echo x"));

        var after = Directory.GetDirectories(ScriptWriter.Directory, "run-*").Length;
        Assert.True(after <= before, $"elevated run directories leaked: {before} -> {after}");
    }

    [Fact]
    public void Invalid_parameters_throw_before_anything_is_launched() =>
        Assert.Throws<ParameterValidationException>(() => new UacProcessRunner(elevate: false)
            .Start(Command("echo x", parameters: new ParameterDef { Name = "host", Required = true })));
}
