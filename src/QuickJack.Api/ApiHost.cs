using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;
using QuickJack.Core.Validation;

namespace QuickJack.Api;

public sealed class ApiOptions
{
    public required QuickJackPaths Paths { get; init; }
    public required CommandStore Store { get; init; }
    public required IRunDispatcher Dispatcher { get; init; }

    public int Port { get; init; } = 47821;

    /// <summary>Mirrors the user setting; API-registered commands land unapproved when true.</summary>
    public Func<bool> RequireApproval { get; init; } = () => true;
}

/// <summary>
/// The loopback registration API. Anything on the machine that holds the token can add a
/// command and see it on the widget a moment later.
/// </summary>
public sealed class ApiHost(ApiOptions options) : IAsyncDisposable
{
    /// <summary>
    /// The SSE endpoint writes to the response body itself rather than going through the
    /// result pipeline, so it does not pick up the configured naming policy. Sharing these
    /// options is what keeps streamed lines shaped like polled ones.
    /// </summary>
    private static readonly JsonSerializerOptions StreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private WebApplication? _app;

    public string? Token { get; private set; }
    public int Port => options.Port;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Token = ApiToken.LoadOrCreate(options.Paths);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.Services.Configure<KestrelServerOptions>(kestrel =>
        {
            // Loopback only. Never IPAddress.Any: this endpoint runs commands.
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 256 * 1024;
        });

        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.PropertyNameCaseInsensitive = true;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        _app = builder.Build();
        Configure(_app);

        await _app.StartAsync(ct);
        ApiToken.WriteEndpointFile(options.Paths, options.Port);
    }

    private void Configure(WebApplication app)
    {
        app.Use(GuardAsync);

        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new
        {
            product = "QuickJack",
            version = typeof(ApiHost).Assembly.GetName().Version?.ToString() ?? "1.0",
            port = options.Port,
            commands = options.Store.Commands.Count,
        }));

        api.MapGet("/commands", () =>
            Results.Ok(options.Store.Commands.Select(CommandResponse.From)));

        api.MapGet("/commands/{id}", (string id) =>
            options.Store.Find(id) is { } found
                ? Results.Ok(CommandResponse.From(found))
                : NotFound(id));

        api.MapPost("/commands", (CommandRequest request, HttpContext http) => UpsertAsync(request, null, http));
        api.MapPut("/commands/{id}", (string id, CommandRequest request, HttpContext http) =>
            UpsertAsync(request, id, http));

        api.MapDelete("/commands/{id}", async (string id) =>
            await options.Store.DeleteAsync(id) ? Results.NoContent() : NotFound(id));

        api.MapPost("/commands/{id}/run", RunAsync);

        api.MapGet("/runs/{runId}", (string runId) =>
            options.Dispatcher.Get(runId) is { } snapshot
                ? Results.Ok(ToResponse(snapshot))
                : Results.NotFound(new ErrorResponse($"No run '{runId}'.")));

        api.MapGet("/runs/{runId}/stream", StreamAsync);
    }

    /// <summary>
    /// Everything that must be true before a request is looked at: the token, and the two
    /// checks that keep a web page from reaching this port.
    /// </summary>
    private async Task GuardAsync(HttpContext http, RequestDelegate next)
    {
        // A browser cannot read the token file, but it can be tricked into sending requests
        // here via DNS rebinding — which defeats "it's only bound to loopback". A real
        // client has no reason to send Origin, so its presence means a browser is calling.
        if (http.Request.Headers.ContainsKey("Origin"))
        {
            await Deny(http, StatusCodes.Status403Forbidden,
                "Browser-originated requests are not accepted.");
            return;
        }

        // The other half of the rebinding defence: only a loopback Host reaches the API.
        var host = http.Request.Host.Host;
        if (!IsLoopbackHost(host))
        {
            await Deny(http, StatusCodes.Status403Forbidden, $"Unexpected Host header '{host}'.");
            return;
        }

        var header = http.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

        if (Token is null || !ApiToken.Matches(Token, presented))
        {
            await Deny(http, StatusCodes.Status401Unauthorized,
                "Provide the token from %APPDATA%\\QuickJack\\api-token as a Bearer token.");
            return;
        }

        await next(http);
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "[::1]" or "::1"
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private async Task<IResult> UpsertAsync(CommandRequest request, string? routeId, HttpContext http)
    {
        var client = http.Request.Headers["X-QuickJack-Client"].ToString();
        var source = string.IsNullOrWhiteSpace(client)
            ? "api:unknown"
            : "api:" + Sanitise(client);

        var id = routeId ?? request.Id ?? string.Empty;
        var existing = id.Length > 0 ? options.Store.Find(id) : null;

        if (existing?.Origin == CommandOrigin.Pinned)
        {
            return Problem(StatusCodes.Status403Forbidden,
                $"'{id}' is pinned by an administrator and cannot be changed through the API.");
        }

        if (!TryParseEnums(request, out var shell, out var elevation, out var output, out var error))
            return Problem(StatusCodes.Status400BadRequest, error!);

        var candidate = new CommandDef
        {
            Id = id,
            Name = request.Name?.Trim() ?? string.Empty,
            Description = request.Description?.Trim(),
            Icon = request.Icon?.Trim(),
            Group = request.Group?.Trim(),
            Shell = shell,
            Script = request.Script ?? string.Empty,
            WorkingDirectory = request.WorkingDirectory,
            Elevation = elevation,
            Output = output,
            ConfirmBeforeRun = request.ConfirmBeforeRun,
            TimeoutSeconds = request.TimeoutSeconds ?? 120,
            Source = source,
            Parameters = (request.Parameters ?? []).Select(p => new ParameterDef
            {
                Name = p.Name ?? string.Empty,
                Label = p.Label,
                Default = p.Default,
                Required = p.Required,
                Pattern = p.Pattern,
            }).ToList(),
        };

        var validation = CommandValidator.Validate(
            candidate,
            existing is null ? options.Store.Commands.Count : options.Store.Commands.Count - 1,
            fromApi: true,
            requireApproval: options.RequireApproval());

        if (!validation.Ok)
        {
            return Problem(validation.Failure switch
            {
                ValidationFailure.Forbidden => StatusCodes.Status403Forbidden,
                ValidationFailure.TooLarge => StatusCodes.Status413PayloadTooLarge,
                ValidationFailure.TooMany => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            }, validation.Message!);
        }

        // Re-approving on every update would let a caller register something innocuous,
        // wait for approval, then quietly swap the script.
        var toSave = validation.Command! with
        {
            Approved = existing is not null
                ? validation.Command!.Approved && ScriptUnchanged(existing, validation.Command!)
                : validation.Command!.Approved,
        };

        var saved = await options.Store.UpsertAsync(toSave);
        return existing is null
            ? Results.Created($"/api/commands/{saved.Id}", CommandResponse.From(saved))
            : Results.Ok(CommandResponse.From(saved));
    }

    private static bool ScriptUnchanged(CommandDef existing, CommandDef candidate) =>
        existing.Script == candidate.Script
        && existing.Shell == candidate.Shell
        && existing.Elevation == candidate.Elevation
        && existing.WorkingDirectory == candidate.WorkingDirectory;

    private async Task<IResult> RunAsync(string id, RunRequest? request)
    {
        if (options.Store.Find(id) is not { } command) return NotFound(id);

        try
        {
            var runId = await options.Dispatcher.StartAsync(command, request?.Arguments);
            return Results.Accepted($"/api/runs/{runId}", new { runId, commandId = command.Id });
        }
        catch (ParameterValidationException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Chiefly "not approved yet" — a 409 says the command exists but is not runnable.
            return Problem(StatusCodes.Status409Conflict, ex.Message);
        }
    }

    /// <summary>
    /// Server-sent events over the buffered snapshot rather than the run's channel: the
    /// channel has a single reader (the widget's output panel), and stealing lines from it
    /// would blank the UI.
    /// </summary>
    private async Task StreamAsync(HttpContext http, string runId)
    {
        if (options.Dispatcher.Get(runId) is null)
        {
            await Deny(http, StatusCodes.Status404NotFound, $"No run '{runId}'.");
            return;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        var sent = 0;

        while (!http.RequestAborted.IsCancellationRequested)
        {
            var snapshot = options.Dispatcher.Get(runId);
            if (snapshot is null) break;

            for (; sent < snapshot.Lines.Count; sent++)
            {
                var line = snapshot.Lines[sent];
                var payload = JsonSerializer.Serialize(
                    new OutputLineResponse(line.Stream.ToString(), line.Text, line.At), StreamJson);

                await http.Response.WriteAsync($"data: {payload}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }

            if (snapshot.State != RunState.Running)
            {
                var done = JsonSerializer.Serialize(new
                {
                    state = snapshot.State.ToString(),
                    exitCode = snapshot.ExitCode,
                }, StreamJson);
                await http.Response.WriteAsync($"event: done\ndata: {done}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                break;
            }

            try { await Task.Delay(150, http.RequestAborted); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static RunResponse ToResponse(RunSnapshot snapshot) => new(
        snapshot.RunId,
        snapshot.CommandId,
        snapshot.State.ToString(),
        snapshot.ExitCode,
        snapshot.Lines.Select(l => new OutputLineResponse(l.Stream.ToString(), l.Text, l.At)).ToList());

    private static bool TryParseEnums(
        CommandRequest request,
        out ShellKind shell,
        out ElevationMode elevation,
        out OutputMode output,
        out string? error)
    {
        shell = ShellKind.PowerShell;
        elevation = ElevationMode.None;
        output = OutputMode.Capture;
        error = null;

        if (!TryParse(request.Shell, "shell", ShellAliases, ref shell, ref error)) return false;
        if (!TryParse(request.Elevation, "elevation", ElevationAliases, ref elevation, ref error)) return false;
        if (!TryParse(request.Output, "output", OutputAliases, ref output, ref error)) return false;

        return true;
    }

    private static bool TryParse<T>(
        string? value, string field, Dictionary<string, T> aliases, ref T result, ref string? error)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        if (aliases.TryGetValue(value.Trim(), out var mapped))
        {
            result = mapped;
            return true;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out result)) return true;

        error = $"'{value}' is not a valid {field}. Expected one of: {string.Join(", ", aliases.Keys)}.";
        return false;
    }

    // Aliases so the obvious spelling works without reading documentation first.
    private static readonly Dictionary<string, ShellKind> ShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ps"] = ShellKind.PowerShell,
        ["powershell"] = ShellKind.PowerShell,
        ["pwsh"] = ShellKind.Pwsh,
        ["powershell7"] = ShellKind.Pwsh,
        ["cmd"] = ShellKind.Cmd,
        ["batch"] = ShellKind.Cmd,
    };

    private static readonly Dictionary<string, ElevationMode> ElevationAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = ElevationMode.None,
        ["normal"] = ElevationMode.None,
        ["uac"] = ElevationMode.Uac,
        ["admin"] = ElevationMode.Uac,
        ["elevated"] = ElevationMode.Uac,
        ["agent"] = ElevationMode.Agent,
    };

    private static readonly Dictionary<string, OutputMode> OutputAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["capture"] = OutputMode.Capture,
        ["inline"] = OutputMode.Capture,
        ["interactive"] = OutputMode.Interactive,
        ["console"] = OutputMode.Interactive,
    };

    /// <summary>Client names end up in the UI, so strip anything that is not plain text.</summary>
    private static string Sanitise(string value)
    {
        var trimmed = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
            .ToArray()).Trim();

        return trimmed.Length switch
        {
            0 => "unknown",
            > 40 => trimmed[..40],
            _ => trimmed,
        };
    }

    private static IResult NotFound(string id) => Results.NotFound(new ErrorResponse($"No command '{id}'."));

    private static IResult Problem(int status, string message) =>
        Results.Json(new ErrorResponse(message), statusCode: status);

    private static async Task Deny(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;

        await _app.StopAsync(TimeSpan.FromSeconds(2));
        await _app.DisposeAsync();
        _app = null;
    }
}
