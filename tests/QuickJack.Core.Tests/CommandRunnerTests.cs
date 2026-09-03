using QuickJack.Core.Execution;
using QuickJack.Core.Models;

namespace QuickJack.Core.Tests;

public class CommandRunnerTests
{
    private static CommandDef Command() => new()
    {
        Id = "test",
        Name = "Test",
        Shell = ShellKind.Cmd,
        Script = "echo hi",
    };

    [Fact]
    public async Task An_approved_local_command_runs()
    {
        var handle = new CommandRunner().Start(Command());
        var result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunState.Succeeded, result.State);
    }

    [Fact]
    public void An_unapproved_command_is_refused()
    {
        // API-registered commands land unapproved so a rogue caller cannot plant a button
        // that runs the moment it is clicked.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CommandRunner().Start(Command() with { Approved = false }));

        Assert.Contains("approved", ex.Message);
    }

    [Fact]
    public void Agent_elevation_from_outside_the_pinned_store_is_refused()
    {
        // CommandStore should make this state unreachable; this is the check that catches
        // it if some other path ever writes one.
        var command = Command() with
        {
            Elevation = ElevationMode.Agent,
            Origin = CommandOrigin.User,
        };

        Assert.Throws<InvalidOperationException>(() => new CommandRunner().Start(command));
    }

    [Fact]
    public void Agent_elevation_without_an_installed_agent_says_so()
    {
        var command = Command() with
        {
            Elevation = ElevationMode.Agent,
            Origin = CommandOrigin.Pinned,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new CommandRunner().Start(command));
        Assert.Contains("agent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_elevation_is_dispatched_to_the_agent_when_one_is_present()
    {
        var agent = new FakeAgent();
        var command = Command() with
        {
            Elevation = ElevationMode.Agent,
            Origin = CommandOrigin.Pinned,
        };

        new CommandRunner(agent).Start(command);

        Assert.Equal(1, agent.Calls);
    }

    private sealed class FakeAgent : IAgentRunner
    {
        public int Calls { get; private set; }

        public RunHandle Start(
            CommandDef command,
            IReadOnlyDictionary<string, string>? arguments = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new LocalProcessRunner().Start(command with { Elevation = ElevationMode.None },
                arguments, cancellationToken);
        }
    }
}
