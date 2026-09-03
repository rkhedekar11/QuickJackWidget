using QuickJack.Core.Execution;
using QuickJack.Core.Models;

namespace QuickJack.Core.Tests;

public class ParameterBinderTests
{
    private static CommandDef Command(string script, params ParameterDef[] parameters) => new()
    {
        Id = "test",
        Name = "Test",
        Shell = ShellKind.Cmd,
        Script = script,
        Parameters = parameters,
    };

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // ---- environment binding (the safe, default path) ----

    [Fact]
    public void Values_are_exposed_as_prefixed_environment_variables()
    {
        var bound = ParameterBinder.Bind(
            Command("echo !QJ_HOST!", new ParameterDef { Name = "host" }),
            Args(("host", "example.com")));

        Assert.Equal("example.com", bound.Environment["QJ_HOST"]);
        Assert.Equal("echo !QJ_HOST!", bound.Script); // script text untouched
    }

    [Fact]
    public void Environment_binding_accepts_values_that_would_be_unsafe_inline()
    {
        // The whole point of the env-var path: nothing is spliced into code, so shell
        // metacharacters are inert and need no validation.
        var bound = ParameterBinder.Bind(
            Command("echo !QJ_ARG!", new ParameterDef { Name = "arg" }),
            Args(("arg", "a & del /q C:\\* | echo")));

        Assert.Equal("a & del /q C:\\* | echo", bound.Environment["QJ_ARG"]);
    }

    [Fact]
    public void Defaults_apply_when_no_value_is_supplied()
    {
        var bound = ParameterBinder.Bind(
            Command("echo x", new ParameterDef { Name = "port", Default = "8080" }));

        Assert.Equal("8080", bound.Environment["QJ_PORT"]);
    }

    [Fact]
    public void Required_parameters_must_have_a_value()
    {
        var command = Command("echo x", new ParameterDef { Name = "host", Required = true });

        Assert.Throws<ParameterValidationException>(() => ParameterBinder.Bind(command));
        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("host", "   "))));
    }

    [Fact]
    public void Undeclared_arguments_are_rejected()
    {
        // Silently ignoring them would hide typos, and accepting them would let a caller
        // inject environment the command never declared.
        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(Command("echo x"), Args(("surprise", "1"))));
    }

    [Fact]
    public void Oversized_and_null_bearing_values_are_rejected()
    {
        var command = Command("echo x", new ParameterDef { Name = "arg" });

        Assert.Throws<ParameterValidationException>(() => ParameterBinder.Bind(
            command, Args(("arg", new string('a', ParameterBinder.MaxValueLength + 1)))));

        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("arg", "a\0b"))));
    }

    [Fact]
    public void Cmd_scripts_must_use_delayed_expansion_for_parameters()
    {
        // cmd expands %VAR% before parsing the line, so a value containing & or | would
        // execute. Unlike PowerShell's $env:X, the cmd environment is not inert.
        var ex = Assert.Throws<ParameterValidationException>(() => ParameterBinder.Bind(
            Command("echo %QJ_HOST%", new ParameterDef { Name = "host" }),
            Args(("host", "example.com"))));

        Assert.Contains("!QJ_HOST!", ex.Message);
    }

    [Fact]
    public void The_percent_form_is_only_refused_for_declared_parameters()
    {
        // %PATH% and friends are the script author's own business.
        var bound = ParameterBinder.Bind(Command("echo %PATH%"));
        Assert.Equal("echo %PATH%", bound.Script);
    }

    [Fact]
    public void PowerShell_scripts_are_unaffected_by_the_cmd_expansion_rule()
    {
        var command = Command("Write-Output $env:QJ_HOST", new ParameterDef { Name = "host" })
            with { Shell = ShellKind.PowerShell };

        var bound = ParameterBinder.Bind(command, Args(("host", "example.com")));
        Assert.Equal("Write-Output $env:QJ_HOST", bound.Script);
    }

    // ---- inline substitution (the gated path) ----

    [Fact]
    public void Substitution_requires_a_declared_pattern()
    {
        var command = Command("ping {{host}}", new ParameterDef { Name = "host" });

        var ex = Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("host", "example.com"))));

        Assert.Contains("validating pattern", ex.Message);
    }

    [Fact]
    public void Substitution_works_when_the_value_matches_the_pattern()
    {
        var bound = ParameterBinder.Bind(
            Command("ping {{host}}", new ParameterDef
            {
                Name = "host",
                Pattern = @"[A-Za-z0-9.\-]+",
            }),
            Args(("host", "example.com")));

        Assert.Equal("ping example.com", bound.Script);
        Assert.Equal("example.com", bound.Environment["QJ_HOST"]); // also available as env
    }

    [Theory]
    [InlineData("example.com & del /q C:\\*")]     // command chaining
    [InlineData("example.com | whoami")]           // piping
    [InlineData("example.com; whoami")]            // statement separator
    [InlineData("$(whoami)")]                      // subshell
    [InlineData("`whoami`")]                       // PowerShell escape / backtick
    [InlineData("example.com\nwhoami")]            // newline injection
    [InlineData("../../etc/passwd")]
    public void Injection_attempts_are_rejected_not_neutralised(string value)
    {
        var command = Command("ping {{host}}", new ParameterDef
        {
            Name = "host",
            Pattern = @"[A-Za-z0-9.\-]+",
        });

        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("host", value))));
    }

    [Fact]
    public void A_permissive_pattern_still_cannot_smuggle_a_newline()
    {
        // [\s\S]* matches anything including newlines, so the pattern alone is no defence.
        // The control-character check is what stops a second statement being appended.
        var command = Command("ping {{host}}", new ParameterDef
        {
            Name = "host",
            Pattern = @"[\s\S]*",
        });

        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("host", "example.com\r\nwhoami"))));

        // ...while an ordinary value on that same permissive pattern still goes through.
        var bound = ParameterBinder.Bind(command, Args(("host", "example.com")));
        Assert.Equal("ping example.com", bound.Script);
    }

    [Fact]
    public void Patterns_are_anchored_so_a_partial_match_does_not_pass()
    {
        var command = Command("echo {{n}}", new ParameterDef { Name = "n", Pattern = @"\d+" });

        Assert.Equal("echo 42", ParameterBinder.Bind(command, Args(("n", "42"))).Script);
        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("n", "42; whoami"))));
    }

    [Fact]
    public void An_unknown_placeholder_is_an_error()
    {
        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(Command("ping {{nope}}")));
    }

    [Fact]
    public void A_catastrophically_backtracking_pattern_times_out_rather_than_hanging()
    {
        var command = Command("echo {{v}}", new ParameterDef
        {
            Name = "v",
            Pattern = "(a+)+$",
        });

        var ex = Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("v", new string('a', 40) + "!"))));

        Assert.Contains("took too long", ex.Message);
    }

    [Fact]
    public void An_invalid_pattern_is_reported_not_thrown_raw()
    {
        var command = Command("echo x", new ParameterDef { Name = "v", Pattern = "([unclosed" });

        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(command, Args(("v", "x"))));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("1leading-digit")]
    [InlineData("has-dash")]
    [InlineData("")]
    public void Invalid_parameter_names_are_rejected(string name)
    {
        Assert.Throws<ParameterValidationException>(
            () => ParameterBinder.Bind(Command("echo x", new ParameterDef { Name = name })));
    }

    [Fact]
    public void Duplicate_parameter_names_are_rejected() =>
        Assert.Throws<ParameterValidationException>(() => ParameterBinder.Bind(
            Command("echo x", new ParameterDef { Name = "a" }, new ParameterDef { Name = "A" })));

    [Fact]
    public void Placeholders_tolerate_inner_whitespace()
    {
        var bound = ParameterBinder.Bind(
            Command("echo {{ n }}", new ParameterDef { Name = "n", Pattern = @"\d+" }),
            Args(("n", "7")));

        Assert.Equal("echo 7", bound.Script);
    }
}
