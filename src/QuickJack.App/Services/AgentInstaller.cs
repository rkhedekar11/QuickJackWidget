using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using QuickJack.Core.Storage;

namespace QuickJack.App.Services;

/// <summary>
/// Installs and removes the elevated agent.
/// <para>
/// The agent is a Scheduled Task running <b>as the interactive user with highest
/// privileges</b>, started at logon. Not a Windows service and not SYSTEM: a service running
/// as SYSTEM would be a far worse escalation surface and would lose the user's profile and
/// PATH, breaking per-user tooling.
/// </para>
/// <para>
/// Installing costs exactly one UAC prompt. That prompt is the whole point — it is what makes
/// a no-prompt admin command something the user consciously granted rather than something a
/// script could arrange on its own.
/// </para>
/// </summary>
public static class AgentInstaller
{
    public const string TaskName = "QuickJack\\Agent";

    public static bool IsInstalled()
    {
        try
        {
            using var process = Elevated.Start("schtasks.exe", ["/query", "/tn", TaskName], elevated: false, capture: true);
            if (process is null) return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Warning text shown before installing. It states the trade plainly rather than burying
    /// it: this is the one place the user gives up a prompt they would otherwise get.
    /// </summary>
    public const string Warning = """
        Enabling no-prompt admin commands installs a background agent that runs with
        administrator rights from the moment you sign in.

        What this changes:
          • Commands you pin will run as administrator with no consent dialog.
          • Only commands you pin can run this way, and pinning each one needs administrator.
          • The agent runs as you, not as SYSTEM, so it can do what you could do by
            clicking through UAC — no more.
          • Every elevated run is recorded in ProgramData\QuickJack\agent.log.

        What you give up:
          • The UAC prompt that would otherwise appear before each elevated command.

        You can remove the agent at any time, and nothing else stops working when you do.
        """;

    public static InstallResult Install(QuickJackPaths paths)
    {
        var agentExe = Elevated.SiblingExecutable("QuickJack.Agent.exe");
        if (agentExe is null || !File.Exists(agentExe))
            return new InstallResult(false, "QuickJack.Agent.exe was not found next to the widget.");

        var widgetExe = Environment.ProcessPath;
        if (widgetExe is null)
            return new InstallResult(false, "Could not determine the widget's own path.");

        // One elevated call does both halves of the setup: lock down the pinned store, and
        // record which executable the agent will accept connections from.
        var secured = Elevated.RunAndWait(agentExe, ["--secure-store", "--client", widgetExe]);
        if (!secured.Success) return secured;

        var xml = BuildTaskXml(agentExe);
        var xmlPath = Path.Combine(Path.GetTempPath(), $"quickjack-agent-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, xml, new System.Text.UnicodeEncoding(false, true));

            var created = Elevated.RunAndWait("schtasks.exe",
                ["/create", "/tn", TaskName, "/xml", xmlPath, "/f"]);

            if (!created.Success) return created;

            // Start it now so the user does not have to sign out and back in.
            Elevated.RunAndWait("schtasks.exe", ["/run", "/tn", TaskName]);

            return new InstallResult(true, "The agent is installed and running.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new InstallResult(false, ex.Message);
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { }
        }
    }

    public static InstallResult Uninstall()
    {
        var ended = Elevated.RunAndWait("schtasks.exe", ["/end", "/tn", TaskName]);
        _ = ended; // an agent that is not running is not an error

        var deleted = Elevated.RunAndWait("schtasks.exe", ["/delete", "/tn", TaskName, "/f"]);

        return deleted.Success
            ? new InstallResult(true, "The agent has been removed.")
            : deleted;
    }

    /// <summary>
    /// Task definition: the interactive user, highest privileges, at logon, restarting on
    /// failure, and none of the power-management conditions that would silently stop it on
    /// a laptop.
    /// </summary>
    private static string BuildTaskXml(string agentExe)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Runs QuickJack commands that you have pinned for no-prompt elevation.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(agentExe)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
