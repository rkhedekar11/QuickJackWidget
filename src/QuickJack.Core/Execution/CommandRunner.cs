using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

/// <summary>
/// The single entry point for running a command. Everything that decides <em>how</em> a
/// command runs — and whether it is allowed to — funnels through here, so there is one
/// place to audit rather than several call sites each making their own choice.
/// </summary>
public sealed class CommandRunner(IAgentRunner? agent = null) : ICommandRunner
{
    private readonly LocalProcessRunner _local = new();
    private readonly UacProcessRunner _uac = new();
    private readonly InteractiveRunner _interactive = new();

    public RunHandle Start(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        // API-registered commands start unapproved. Enforcing that here as well as in the
        // UI means a new call site cannot accidentally skip the check.
        if (!command.CanRun)
        {
            throw new InvalidOperationException(
                $"'{command.Name}' has not been approved yet. Approve it in the widget first.");
        }

        if (command.Elevation == ElevationMode.Agent && command.Origin != CommandOrigin.Pinned)
        {
            // Reaching here means something wrote an agent command into the user store,
            // which CommandStore is supposed to make impossible.
            throw new InvalidOperationException(
                $"'{command.Name}' claims agent elevation but did not come from the pinned store.");
        }

        if (command.Output == OutputMode.Interactive)
            return _interactive.Start(command, arguments, cancellationToken);

        return command.Elevation switch
        {
            ElevationMode.None => _local.Start(command, arguments, cancellationToken),
            ElevationMode.Uac => _uac.Start(command, arguments, cancellationToken),
            ElevationMode.Agent => (agent ?? throw new InvalidOperationException(
                    "No-prompt admin commands need the QuickJack agent, which is not installed."))
                .Start(command, arguments, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }
}

/// <summary>
/// Marker for the elevated-agent transport, implemented in M5. Kept separate so
/// <see cref="CommandRunner"/> works with or without the agent installed.
/// </summary>
public interface IAgentRunner : ICommandRunner;
