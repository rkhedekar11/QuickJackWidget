using QuickJack.Core.Storage;

namespace QuickJack.Agent;

/// <summary>
/// Entry point for the elevated agent. Launched by a Scheduled Task at logon with "run with
/// highest privileges"; not intended to be started by hand.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = QuickJackPaths.Default;
        var log = new AgentLog(paths);

        if (args.Contains("--secure-store", StringComparer.OrdinalIgnoreCase))
            return SecureStore(paths, log, args);

        if (!WindowsAcl.IsCurrentProcessElevated())
        {
            log.Error("The agent must run elevated. It is normally started by its scheduled task.");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        try
        {
            await new AgentServer(paths, log).RunAsync(stopping.Token);
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Agent terminated unexpectedly", ex);
            return 1;
        }
    }

    /// <summary>
    /// One-shot setup, invoked elevated by the installer: lock the machine directory down and
    /// record which executable is allowed to talk to the agent.
    /// </summary>
    private static int SecureStore(QuickJackPaths paths, AgentLog log, string[] args)
    {
        if (!WindowsAcl.IsCurrentProcessElevated())
        {
            Console.Error.WriteLine("--secure-store requires administrator.");
            return 2;
        }

        try
        {
            PinnedStore.SecureDirectory(paths);

            var clientIndex = Array.FindIndex(args,
                a => a.Equals("--client", StringComparison.OrdinalIgnoreCase));

            if (clientIndex >= 0 && clientIndex + 1 < args.Length)
            {
                File.WriteAllText(PinnedStore.ClientPathFile(paths), args[clientIndex + 1].Trim());
            }

            if (!File.Exists(paths.PinnedCommands)) PinnedStore.Write(paths, []);

            log.Info($"Secured {paths.MachineDirectory}.");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Could not secure the pinned store", ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

/// <summary>
/// Append-only record of everything the agent was asked to run. It lives in the admin-owned
/// directory, so a process running as the user cannot erase evidence of what it requested.
/// </summary>
public sealed class AgentLog(QuickJackPaths paths)
{
    private readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.MachineDirectory);
                File.AppendAllText(
                    paths.AgentLog,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let logging be the thing that stops the agent.
            }
        }
    }
}
