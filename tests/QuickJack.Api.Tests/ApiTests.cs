using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QuickJack.Core.Models;

namespace QuickJack.Api.Tests;

public class ApiTests
{
    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static object FlushDns(object? extra = null) => new
    {
        name = "Flush DNS",
        shell = "cmd",
        script = "ipconfig /flushdns",
    };

    // ---- authentication ----

    [Fact]
    public async Task A_request_without_a_token_is_rejected()
    {
        await using var api = await ApiFixture.CreateAsync();
        using var anonymous = api.Anonymous();

        var response = await anonymous.GetAsync("/api/commands");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_the_wrong_token_is_rejected()
    {
        await using var api = await ApiFixture.CreateAsync();
        using var wrong = api.Anonymous();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        var response = await wrong.GetAsync("/api/commands");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_is_accepted()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_token_file_is_readable_only_by_the_current_user()
    {
        await using var api = await ApiFixture.CreateAsync();

        var security = new FileInfo(api.Paths.ApiToken).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

        // The token's whole value is that nothing else can read it, so inheritance must be
        // off and the only entry must be the current user.
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(1, rules.Count);
    }

    // ---- browser defences ----

    [Fact]
    public async Task A_request_carrying_an_Origin_header_is_refused()
    {
        // A browser cannot read the token file, but DNS rebinding can make it send requests
        // to this port. A real client never sends Origin.
        await using var api = await ApiFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/commands");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_a_non_loopback_Host_header_is_refused()
    {
        await using var api = await ApiFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/commands");
        request.Headers.Host = "attacker.example";

        var response = await api.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the elevation invariant ----

    [Fact]
    public async Task The_API_can_never_create_a_no_prompt_admin_command()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Backdoor",
            shell = "cmd",
            script = "whoami",
            elevation = "agent",
        }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(api.Store.Commands);
    }

    [Fact]
    public async Task The_API_may_create_a_UAC_command()
    {
        // Allowed, because running it still puts a Windows consent dialog in front of the user.
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Flush DNS",
            shell = "cmd",
            script = "ipconfig /flushdns",
            elevation = "admin",
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ElevationMode.Uac, api.Store.Find("flush-dns")!.Elevation);
    }

    // ---- registration ----

    [Fact]
    public async Task Registering_a_command_makes_it_appear_in_the_store()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var saved = Assert.Single(api.Store.Commands);
        Assert.Equal("flush-dns", saved.Id);
        Assert.Equal("ipconfig /flushdns", saved.Script);
        Assert.Equal(ShellKind.Cmd, saved.Shell);
    }

    [Fact]
    public async Task A_registered_command_lands_unapproved()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: true);

        await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        Assert.False(Assert.Single(api.Store.Commands).Approved);
    }

    [Fact]
    public async Task Approval_can_be_switched_off()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: false);

        await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        Assert.True(Assert.Single(api.Store.Commands).Approved);
    }

    [Fact]
    public async Task The_registering_client_is_recorded()
    {
        // So an API-planted button is never anonymous in the UI.
        await using var api = await ApiFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/commands")
        {
            Content = Json(FlushDns()),
        };
        request.Headers.Add("X-QuickJack-Client", "deploy-script");

        await api.Client.SendAsync(request);

        Assert.Equal("api:deploy-script", Assert.Single(api.Store.Commands).Source);
    }

    [Fact]
    public async Task A_hostile_client_name_is_stripped()
    {
        await using var api = await ApiFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/commands")
        {
            Content = Json(FlushDns()),
        };
        request.Headers.Add("X-QuickJack-Client", "<b>evil</b>");

        await api.Client.SendAsync(request);

        var source = Assert.Single(api.Store.Commands).Source;
        Assert.DoesNotContain('<', source);
        Assert.DoesNotContain('>', source);
    }

    [Fact]
    public async Task Re_registering_an_approved_command_with_a_new_script_revokes_approval()
    {
        // Otherwise a caller could register something innocuous, wait for approval, then
        // quietly swap the script for something else.
        await using var api = await ApiFixture.CreateAsync(requireApproval: false);

        await api.Client.PostAsync("/api/commands", Json(FlushDns()));
        Assert.True(api.Store.Find("flush-dns")!.Approved);

        await using var strict = await ApiFixture.CreateAsync(requireApproval: true);
        await strict.Client.PostAsync("/api/commands", Json(FlushDns()));
        await strict.Client.PutAsync("/api/commands/flush-dns", Json(new
        {
            name = "Flush DNS",
            shell = "cmd",
            script = "something-else.exe",
        }));

        Assert.False(strict.Store.Find("flush-dns")!.Approved);
    }

    // ---- validation ----

    [Fact]
    public async Task A_command_without_a_script_is_rejected()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new { name = "Nothing" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_script_is_rejected()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Huge",
            shell = "cmd",
            script = new string('x', 65 * 1024),
        }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_shell_gives_a_readable_error_not_a_deserialisation_failure()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Odd",
            shell = "bash",
            script = "ls",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a valid shell", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_cmd_script_using_percent_expansion_is_rejected_at_registration()
    {
        // Caught when it is added rather than at 2am when someone clicks it.
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Ping",
            shell = "cmd",
            script = "ping %QJ_HOST%",
            parameters = new[] { new { name = "host" } },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("!QJ_HOST!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_substituted_parameter_without_a_pattern_is_rejected_at_registration()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Ping",
            shell = "ps",
            script = "ping {{host}}",
            parameters = new[] { new { name = "host" } },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validating pattern", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_substituted_parameter_with_a_pattern_is_accepted()
    {
        await using var api = await ApiFixture.CreateAsync();

        var response = await api.Client.PostAsync("/api/commands", Json(new
        {
            name = "Ping",
            shell = "ps",
            script = "ping {{host}}",
            parameters = new[] { new { name = "host", pattern = @"[A-Za-z0-9.\-]+" } },
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- CRUD ----

    [Fact]
    public async Task Commands_can_be_listed_fetched_and_deleted()
    {
        await using var api = await ApiFixture.CreateAsync();
        await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        var list = await api.Client.GetFromJsonAsync<List<CommandResponse>>("/api/commands");
        Assert.Single(list!);

        var one = await api.Client.GetAsync("/api/commands/flush-dns");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await api.Client.DeleteAsync("/api/commands/flush-dns")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await api.Client.DeleteAsync("/api/commands/flush-dns")).StatusCode);
        Assert.Empty(api.Store.Commands);
    }

    [Fact]
    public async Task An_unknown_command_is_a_404()
    {
        await using var api = await ApiFixture.CreateAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await api.Client.GetAsync("/api/commands/nope")).StatusCode);
    }

    // ---- running ----

    [Fact]
    public async Task Running_a_command_dispatches_it_to_the_widget()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: false);
        await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        var response = await api.Client.PostAsync("/api/commands/flush-dns/run",
            Json(new { arguments = new Dictionary<string, string>() }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The API must never execute anything itself; it goes through the widget so the run
        // obeys the same approval and elevation checks as a click.
        var started = Assert.Single(api.Dispatcher.Started);
        Assert.Equal("flush-dns", started.Command.Id);
    }

    [Fact]
    public async Task Running_an_unapproved_command_is_a_conflict_not_a_silent_success()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: true);
        await api.Client.PostAsync("/api/commands", Json(FlushDns()));

        api.Dispatcher.ThrowOnStart =
            new InvalidOperationException("'Flush DNS' has not been approved yet.");

        var response = await api.Client.PostAsync("/api/commands/flush-dns/run", Json(new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("approved", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_run_can_be_polled_for_output()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: false);
        await api.Client.PostAsync("/api/commands", Json(FlushDns()));
        await api.Client.PostAsync("/api/commands/flush-dns/run", Json(new { }));

        var run = await api.Client.GetFromJsonAsync<RunResponse>("/api/runs/run1");

        Assert.Equal("Succeeded", run!.State);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("fake output", Assert.Single(run.Lines).Text);
    }

    [Fact]
    public async Task An_unknown_run_is_a_404()
    {
        await using var api = await ApiFixture.CreateAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await api.Client.GetAsync("/api/runs/nope")).StatusCode);
    }

    [Fact]
    public async Task Output_can_be_streamed_as_server_sent_events()
    {
        await using var api = await ApiFixture.CreateAsync(requireApproval: false);
        await api.Client.PostAsync("/api/commands", Json(FlushDns()));
        await api.Client.PostAsync("/api/commands/flush-dns/run", Json(new { }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var body = await api.Client.GetStringAsync("/api/runs/run1/stream", cts.Token);

        Assert.Contains("fake output", body);
        Assert.Contains("event: done", body);

        // Streamed lines must be shaped like polled ones - the SSE path writes to the
        // response body directly and does not inherit the configured naming policy.
        Assert.Contains("\"stream\"", body);
        Assert.Contains("\"text\"", body);
        Assert.DoesNotContain("\"Stream\"", body);
    }
}
