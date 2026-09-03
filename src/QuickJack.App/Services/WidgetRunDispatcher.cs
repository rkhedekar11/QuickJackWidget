using System.Collections.Concurrent;
using System.Windows.Threading;
using QuickJack.Api;
using QuickJack.App.ViewModels;
using QuickJack.Core.Models;

namespace QuickJack.App.Services;

/// <summary>
/// Lets the HTTP API start runs through the widget rather than beside it, so an API-triggered
/// command obeys the same approval and elevation rules as a click and shows up in the same
/// output panel.
/// </summary>
public sealed class WidgetRunDispatcher(WidgetViewModel viewModel, Dispatcher dispatcher) : IRunDispatcher
{
    private const int MaxRemembered = 50;

    private readonly ConcurrentDictionary<string, RunViewModel> _runs = new();
    private readonly ConcurrentQueue<string> _order = new();

    public async Task<string> StartAsync(CommandDef command, IReadOnlyDictionary<string, string>? arguments)
    {
        // Runs touch observable collections bound to the UI, so they must start on the
        // dispatcher thread even though the request arrives on a Kestrel thread.
        var run = await dispatcher.InvokeAsync(() => viewModel.StartForApi(command, arguments));

        _runs[run.RunId] = run;
        _order.Enqueue(run.RunId);
        Trim();

        return run.RunId;
    }

    public RunSnapshot? Get(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run)) return null;

        // Reading the bound collection from a Kestrel thread would race the UI, so take the
        // snapshot on the dispatcher.
        return dispatcher.Invoke(() => new RunSnapshot(
            run.RunId,
            run.CommandId,
            run.State,
            run.ExitCode,
            run.Lines.Select(l => l.Raw).ToList()));
    }

    /// <summary>Keeps the most recent runs queryable without growing without bound.</summary>
    private void Trim()
    {
        while (_order.Count > MaxRemembered && _order.TryDequeue(out var oldest))
            _runs.TryRemove(oldest, out _);
    }
}
