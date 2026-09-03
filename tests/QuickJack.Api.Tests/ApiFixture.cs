using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net;
using QuickJack.Api;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Api.Tests;

/// <summary>
/// A real ApiHost on a free loopback port with isolated state.
/// <para>
/// It is hosted for real rather than through a test server because half of what is being
/// tested — loopback-only binding, the Host header check — only exists at the transport
/// level and an in-memory server would skip it entirely.
/// </para>
/// </summary>
public sealed class ApiFixture : IAsyncDisposable
{
    private readonly string _root;

    private ApiFixture(string root, QuickJackPaths paths, CommandStore store, ApiHost host, FakeDispatcher dispatcher)
    {
        _root = root;
        Paths = paths;
        Store = store;
        Host = host;
        Dispatcher = dispatcher;

        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
    }

    public QuickJackPaths Paths { get; }
    public CommandStore Store { get; }
    public ApiHost Host { get; }
    public FakeDispatcher Dispatcher { get; }
    public HttpClient Client { get; }

    /// <summary>A client with no Authorization header.</summary>
    public HttpClient Anonymous() => new() { BaseAddress = Client.BaseAddress };

    public static async Task<ApiFixture> CreateAsync(bool requireApproval = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "quickjack-api-tests", Guid.NewGuid().ToString("N"));
        var paths = QuickJackPaths.Under(root);
        Directory.CreateDirectory(paths.UserDirectory);
        Directory.CreateDirectory(paths.MachineDirectory);

        var store = new CommandStore(paths);
        await store.ReloadAsync();

        var dispatcher = new FakeDispatcher();
        var host = new ApiHost(new ApiOptions
        {
            Paths = paths,
            Store = store,
            Dispatcher = dispatcher,
            Port = FreePort(),
            RequireApproval = () => requireApproval,
        });

        await host.StartAsync();
        return new ApiFixture(root, paths, store, host, dispatcher);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        Store.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Records dispatch calls without starting real processes.</summary>
public sealed class FakeDispatcher : IRunDispatcher
{
    private readonly Dictionary<string, RunSnapshot> _runs = [];

    public List<(CommandDef Command, IReadOnlyDictionary<string, string>? Arguments)> Started { get; } = [];

    /// <summary>When set, StartAsync throws it — used to exercise the refusal paths.</summary>
    public Exception? ThrowOnStart { get; set; }

    public Task<string> StartAsync(CommandDef command, IReadOnlyDictionary<string, string>? arguments)
    {
        if (ThrowOnStart is not null) throw ThrowOnStart;

        Started.Add((command, arguments));

        var runId = "run" + Started.Count;
        _runs[runId] = new RunSnapshot(runId, command.Id, RunState.Succeeded, 0,
            [OutputLine.Out("fake output")]);

        return Task.FromResult(runId);
    }

    public RunSnapshot? Get(string runId) => _runs.GetValueOrDefault(runId);
}
