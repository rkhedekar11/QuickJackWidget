using System.IO;
using QuickJack.Core.Models;

namespace QuickJack.App.Services;

/// <summary>
/// Pins a command for no-prompt elevation, and unpins it again.
/// <para>
/// A pinned command lives in the Administrators-write pinned store, so creating one costs a
/// UAC prompt — and that is the property the whole no-prompt design rests on. Without it,
/// anything running as the user could plant a silent administrator button and wait for a
/// click. So this deliberately does <em>not</em> try to be clever about batching or
/// remembering consent: one pin, one prompt.
/// </para>
/// <para>
/// Only the command's id crosses into the elevated helper, which reads the definition out of
/// the user's store itself. Handing it script text would let a caller pin something other
/// than what the user was shown before the prompt appeared.
/// </para>
/// </summary>
public static class PinService
{
    /// <summary>Shown before the prompt, so the user is agreeing to something specific.</summary>
    public static string Warning(CommandDef command) => $"""
        Pin "{command.Name}" so it runs as administrator with no prompt?

        You will be asked for administrator once, now, to pin it. After that this command
        runs elevated whenever it is clicked, without a consent dialog.

        It will run:
          {Summarise(command.Script)}

        Every run is recorded in ProgramData\QuickJack\agent.log, and you can unpin it at
        any time.
        """;

    public static InstallResult Pin(CommandDef command)
    {
        if (!command.Approved)
        {
            // Pinning something the user has never reviewed is exactly the escalation the
            // approval gate exists to stop.
            return new InstallResult(false,
                $"Approve \"{command.Name}\" before pinning it.");
        }

        return Invoke("--pin", command.Id);
    }

    public static InstallResult Unpin(string id) => Invoke("--unpin", id);

    private static InstallResult Invoke(string verb, string id)
    {
        var agentExe = Elevated.SiblingExecutable("QuickJack.Agent.exe");

        if (agentExe is null || !File.Exists(agentExe))
            return new InstallResult(false, "QuickJack.Agent.exe was not found next to the widget.");

        var result = Elevated.RunAndWait(agentExe, [verb, id]);

        if (!result.Success) return result;

        return new InstallResult(true, verb == "--pin"
            ? "Pinned. It now runs elevated with no prompt."
            : "Unpinned. It will ask for consent again, if it asks at all.");
    }

    /// <summary>The script as one short line, for a dialog that has to stay readable.</summary>
    private static string Summarise(string script)
    {
        var lines = script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var flattened = string.Join("  ⏎  ", lines);

        return flattened.Length <= 160 ? flattened : flattened[..160] + "…";
    }
}
