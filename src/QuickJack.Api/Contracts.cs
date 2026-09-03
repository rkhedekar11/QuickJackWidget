using QuickJack.Core.Execution;
using QuickJack.Core.Models;

namespace QuickJack.Api;

// ---- requests ----

public sealed class ParameterRequest
{
    public string? Name { get; set; }
    public string? Label { get; set; }
    public string? Default { get; set; }
    public bool Required { get; set; }
    public string? Pattern { get; set; }
}

/// <summary>
/// The JSON a client posts. Deliberately all-optional and string-typed for the enums: a
/// caller should get a readable error, not a deserialization failure, for a typo.
/// </summary>
public sealed class CommandRequest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Group { get; set; }
    public string? Shell { get; set; }
    public string? Script { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Elevation { get; set; }
    public string? Output { get; set; }
    public bool ConfirmBeforeRun { get; set; }
    public int? TimeoutSeconds { get; set; }
    public List<ParameterRequest>? Parameters { get; set; }
}

public sealed class RunRequest
{
    public Dictionary<string, string>? Arguments { get; set; }
}

// ---- responses ----

public sealed record CommandResponse(
    string Id,
    string Name,
    string? Description,
    string? Icon,
    string? Group,
    string Shell,
    string Script,
    string Elevation,
    string Output,
    bool Approved,
    string Source,
    string Origin,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CommandResponse From(CommandDef c) => new(
        c.Id, c.Name, c.Description, c.Icon, c.Group,
        c.Shell.ToString(), c.Script, c.Elevation.ToString(), c.Output.ToString(),
        c.Approved, c.Source, c.Origin.ToString(), c.CreatedAt, c.UpdatedAt);
}

public sealed record OutputLineResponse(string Stream, string Text, DateTimeOffset At);

public sealed record RunResponse(
    string RunId,
    string CommandId,
    string State,
    int? ExitCode,
    IReadOnlyList<OutputLineResponse> Lines);

public sealed record ErrorResponse(string Error);

// ---- the seam between the API and the widget ----

public sealed record RunSnapshot(
    string RunId,
    string CommandId,
    RunState State,
    int? ExitCode,
    IReadOnlyList<OutputLine> Lines);

/// <summary>
/// How the API asks the widget to run something.
/// <para>
/// The API never executes a command itself. Runs go through the widget so they appear in
/// its output panel and obey the same approval and elevation checks as a click — there is
/// one execution path, not two.
/// </para>
/// </summary>
public interface IRunDispatcher
{
    /// <summary>Starts a run and returns its id. Throws if the command may not run.</summary>
    Task<string> StartAsync(CommandDef command, IReadOnlyDictionary<string, string>? arguments);

    /// <summary>Current state and buffered output, or null if the run id is unknown.</summary>
    RunSnapshot? Get(string runId);
}
