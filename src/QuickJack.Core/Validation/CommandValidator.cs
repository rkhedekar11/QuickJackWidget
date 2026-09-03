using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Core.Validation;

public enum ValidationFailure
{
    None,
    Invalid,        // 400 — malformed or missing fields
    Forbidden,      // 403 — asked for something the caller may never have
    TooLarge,       // 413 — script over the cap
    TooMany,        // 409 — command cap reached
}

public sealed record ValidationResult(ValidationFailure Failure, string? Message = null, CommandDef? Command = null)
{
    public bool Ok => Failure == ValidationFailure.None;

    public static ValidationResult Success(CommandDef command) => new(ValidationFailure.None, null, command);
    public static ValidationResult Invalid(string message) => new(ValidationFailure.Invalid, message);
    public static ValidationResult Forbidden(string message) => new(ValidationFailure.Forbidden, message);
    public static ValidationResult TooLarge(string message) => new(ValidationFailure.TooLarge, message);
    public static ValidationResult TooMany(string message) => new(ValidationFailure.TooMany, message);
}

/// <summary>
/// Everything a command must satisfy before it is allowed into the store. Kept in one place
/// so the rules are auditable rather than scattered across endpoint handlers.
/// </summary>
public static class CommandValidator
{
    public const int MaxScriptBytes = 64 * 1024;
    public const int MaxCommands = 200;
    public const int MaxNameLength = 120;
    public const int MaxParameters = 16;

    /// <summary>
    /// Validates a command arriving from the HTTP API.
    /// </summary>
    /// <param name="fromApi">
    /// True for API callers, who are held to stricter rules than the UI — most importantly
    /// they can never mint an agent-elevated command.
    /// </param>
    public static ValidationResult Validate(
        CommandDef candidate,
        int existingCount,
        bool fromApi,
        bool requireApproval)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
            return ValidationResult.Invalid("A command needs a name.");

        if (candidate.Name.Length > MaxNameLength)
            return ValidationResult.Invalid($"Name must be {MaxNameLength} characters or fewer.");

        if (string.IsNullOrWhiteSpace(candidate.Script))
            return ValidationResult.Invalid("A command needs a script.");

        if (System.Text.Encoding.UTF8.GetByteCount(candidate.Script) > MaxScriptBytes)
            return ValidationResult.TooLarge($"Scripts must be {MaxScriptBytes / 1024} KB or smaller.");

        if (candidate.Id.Length > 0 && !Slug.IsValid(candidate.Id))
            return ValidationResult.Invalid(
                "Id must be lowercase letters, digits and hyphens, e.g. 'flush-dns'.");

        if (candidate.Parameters.Count > MaxParameters)
            return ValidationResult.Invalid($"A command may declare at most {MaxParameters} parameters.");

        if (existingCount >= MaxCommands)
            return ValidationResult.TooMany($"QuickJack holds at most {MaxCommands} commands.");

        if (candidate.TimeoutSeconds is < 0 or > 24 * 60 * 60)
            return ValidationResult.Invalid("Timeout must be between 0 and 86400 seconds.");

        // The invariant this whole design rests on. An agent-elevated command runs as
        // administrator with no prompt; letting anything that can reach a loopback port
        // create one would make QuickJack a privilege-escalation service. There is no flag
        // and no configuration that relaxes this.
        if (fromApi && candidate.Elevation == ElevationMode.Agent)
        {
            return ValidationResult.Forbidden(
                "The API cannot create no-prompt admin commands. Register it as 'uac' " +
                "instead, or pin it from the widget (which itself requires administrator).");
        }

        // Catch a bad script at registration time rather than at 2am when it is clicked.
        // Only the value-independent checks: a placeholder value good enough to satisfy one
        // parameter pattern would fail another.
        try
        {
            ParameterBinder.ValidateDefinition(candidate);
        }
        catch (ParameterValidationException ex)
        {
            return ValidationResult.Invalid(ex.Message);
        }

        return ValidationResult.Success(candidate with
        {
            // An API-registered command must be approved once in the widget before it can
            // run, so a rogue caller cannot silently plant a button the user fat-fingers.
            Approved = !fromApi || !requireApproval,
        });
    }
}
