using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickJack.Core.Execution;

namespace QuickJack.App.ViewModels;

public sealed partial class OutputLineViewModel(OutputLine line)
{
    public OutputLine Raw { get; } = line;
    public string Text { get; } = line.Text;
    public OutputStream Stream { get; } = line.Stream;
    public bool IsError => Stream == OutputStream.StdErr;
    public bool IsInfo => Stream == OutputStream.Info;
}

/// <summary>One execution, as the output panel sees it.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    private readonly RunHandle _handle;
    private readonly DispatcherTimer _ticker;

    [ObservableProperty] private RunState _state = RunState.Running;
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private string _elapsed = "0.0s";

    public RunViewModel(RunHandle handle)
    {
        _handle = handle;
        CommandName = handle.Command.Name;
        Started = DateTimeOffset.Now;

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _ticker.Tick += (_, _) => Elapsed = $"{(DateTimeOffset.Now - Started).TotalSeconds:0.0}s";
        _ticker.Start();

        _ = PumpAsync();
    }

    public string RunId => _handle.RunId;
    public string CommandId => _handle.Command.Id;
    public string CommandName { get; }
    public DateTimeOffset Started { get; }
    public ObservableCollection<OutputLineViewModel> Lines { get; } = [];

    public bool IsRunning => State == RunState.Running;

    public string StatusText => State switch
    {
        RunState.Running => "running",
        RunState.Succeeded => $"exit {ExitCode ?? 0}",
        RunState.Failed => $"exit {ExitCode?.ToString() ?? "?"}",
        RunState.Cancelled => "cancelled",
        RunState.TimedOut => "timed out",
        RunState.Faulted => "failed to start",
        _ => State.ToString().ToLowerInvariant(),
    };

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _handle.Cancel();

    [RelayCommand]
    private void Copy()
    {
        var text = new StringBuilder();
        foreach (var line in Lines) text.AppendLine(line.Text);

        try { Clipboard.SetText(text.ToString()); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open; not worth surfacing.
        }
    }

    private async Task PumpAsync()
    {
        // The runner writes from a background thread; every touch of Lines has to land on
        // the dispatcher or WPF will throw on the collection change.
        await foreach (var line in _handle.Output.ReadAllAsync())
        {
            var vm = new OutputLineViewModel(line);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Lines.Add(vm);

                // A runaway command can emit output indefinitely; keeping every line would
                // eventually take the widget down with it.
                if (Lines.Count > MaxLines) Lines.RemoveAt(0);
            });
        }

        var result = await _handle.Completion;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _ticker.Stop();
            State = result.State;
            ExitCode = result.ExitCode;
            Elapsed = $"{result.Duration.TotalSeconds:0.0}s";
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusText));
            CancelCommand.NotifyCanExecuteChanged();
        });
    }

    partial void OnStateChanged(RunState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
    }
}
