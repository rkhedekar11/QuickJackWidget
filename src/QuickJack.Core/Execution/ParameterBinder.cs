using System.Text.RegularExpressions;
using QuickJack.Core.Models;

namespace QuickJack.Core.Execution;

public sealed class ParameterValidationException(string message) : Exception(message);

/// <summary>Script text plus the environment it should run with.</summary>
public sealed record BoundScript(string Script, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Turns user-supplied parameter values into something safe to run.
/// <para>
/// The default and documented path is environment variables: a parameter named
/// <c>host</c> becomes <c>QJ_HOST</c>, and the script reads <c>$env:QJ_HOST</c>. Nothing is
/// spliced into script text, so nothing can escape into code.
/// </para>
/// <para>
/// Inline <c>{{host}}</c> substitution is also supported, because it is what people reach
/// for — but only for parameters that declare a validating <see cref="ParameterDef.Pattern"/>,
/// and the value must additionally be free of control characters. A permissive pattern
/// (<c>[\s\S]*</c>) would otherwise let a newline smuggle in a second command.
/// </para>
/// <para>This is the most security-relevant code in the app. Its tests are load-bearing.</para>
/// </summary>
public static class ParameterBinder
{
    public const string EnvPrefix = "QJ_";
    public const int MaxValueLength = 4096;

    // A user-authored pattern could backtrack catastrophically; cap every match attempt.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex ParameterNamePattern =
        new(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.Compiled);

    public static BoundScript Bind(
        CommandDef command,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        arguments ??= new Dictionary<string, string>();

        var declared = new Dictionary<string, ParameterDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in command.Parameters)
        {
            if (!ParameterNamePattern.IsMatch(p.Name))
                throw new ParameterValidationException($"Invalid parameter name '{p.Name}'.");
            if (!declared.TryAdd(p.Name, p))
                throw new ParameterValidationException($"Duplicate parameter '{p.Name}'.");
        }

        // Undeclared arguments are rejected rather than ignored: silently dropping them
        // hides typos, and accepting them would let a caller inject arbitrary environment.
        foreach (var key in arguments.Keys)
        {
            if (!declared.ContainsKey(key))
                throw new ParameterValidationException($"Unknown parameter '{key}'.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, def) in declared)
        {
            if (!arguments.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
                value = def.Default ?? string.Empty;

            if (def.Required && string.IsNullOrWhiteSpace(value))
                throw new ParameterValidationException($"Parameter '{name}' is required.");

            if (value.Length > MaxValueLength)
                throw new ParameterValidationException(
                    $"Parameter '{name}' exceeds {MaxValueLength} characters.");

            if (value.Contains('\0'))
                throw new ParameterValidationException($"Parameter '{name}' contains a null byte.");

            if (def.Pattern is { Length: > 0 } pattern && !MatchesFully(pattern, value, name))
                throw new ParameterValidationException(
                    $"Parameter '{name}' does not match the required pattern.");

            values[name] = value;
            environment[EnvPrefix + name.ToUpperInvariant()] = value;
        }

        if (command.Shell == ShellKind.Cmd) RejectImmediateExpansion(command.Script, declared.Keys);

        var script = Substitute(command.Script, declared, values);
        return new BoundScript(script, environment);
    }

    /// <summary>
    /// cmd expands <c>%VAR%</c> while parsing the line, so a value containing <c>&amp;</c>,
    /// <c>|</c> or <c>&gt;</c> becomes a second command — the environment is <em>not</em>
    /// inert in cmd the way <c>$env:X</c> is in PowerShell. Delayed expansion
    /// (<c>!VAR!</c>, enabled by the script preamble) happens after parsing and is safe, so
    /// the unsafe form is rejected rather than quietly allowed.
    /// </summary>
    private static void RejectImmediateExpansion(string script, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var env = EnvPrefix + name.ToUpperInvariant();
            if (script.Contains($"%{env}%", StringComparison.OrdinalIgnoreCase))
            {
                throw new ParameterValidationException(
                    $"In a cmd script, read '{name}' as !{env}! rather than %{env}%. " +
                    "cmd expands the %-form before parsing the line, so a value containing " +
                    "& or | would run as a second command.");
            }
        }
    }

    private static string Substitute(
        string script,
        IReadOnlyDictionary<string, ParameterDef> declared,
        IReadOnlyDictionary<string, string> values)
    {
        if (!script.Contains("{{", StringComparison.Ordinal)) return script;

        return PlaceholderPattern.Replace(script, match =>
        {
            var name = match.Groups["name"].Value;

            if (!declared.TryGetValue(name, out var def))
                throw new ParameterValidationException(
                    $"Script references '{{{{{name}}}}}' but no such parameter is declared.");

            if (string.IsNullOrEmpty(def.Pattern))
                throw new ParameterValidationException(
                    $"Parameter '{name}' is substituted into the script, so it must declare a " +
                    "validating pattern. Read it from the environment instead " +
                    $"(${{env:{EnvPrefix}{name.ToUpperInvariant()}}}) to avoid the restriction.");

            var value = values[name];

            // Belt and braces on top of the pattern: a permissive pattern would otherwise
            // let a newline or carriage return start a second statement.
            foreach (var ch in value)
            {
                if (char.IsControl(ch))
                    throw new ParameterValidationException(
                        $"Parameter '{name}' is substituted into the script and cannot " +
                        "contain control characters.");
            }

            return value;
        });
    }

    private static bool MatchesFully(string pattern, string value, string name)
    {
        try
        {
            // Anchored so a partial match cannot pass: "\d+" must mean the whole value.
            return Regex.IsMatch(value, $@"\A(?:{pattern})\z", RegexOptions.None, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ParameterValidationException(
                $"Pattern for parameter '{name}' took too long to evaluate.");
        }
        catch (ArgumentException)
        {
            throw new ParameterValidationException($"Parameter '{name}' has an invalid pattern.");
        }
    }
}
