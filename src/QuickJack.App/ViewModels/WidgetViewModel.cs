using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickJack.App.Search;
using QuickJack.App.Services;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.App.ViewModels;

public enum PaletteMode
{
    Browsing,
    CollectingParameters,
    Confirming,
    Output,
}

/// <summary>What the confirm pane is currently asking about.</summary>
public enum PendingAction
{
    Run,
    Approve,
    Pin,
    Unpin,
}

public sealed partial class ParameterInputViewModel(ParameterDef def) : ObservableObject
{
    [ObservableProperty] private string _value = def.Default ?? string.Empty;

    public ParameterDef Definition { get; } = def;
    public string Name => Definition.Name;
    public string Label => string.IsNullOrWhiteSpace(Definition.Label) ? Definition.Name : Definition.Label!;
    public bool Required => Definition.Required;

    /// <summary>Shown so the user knows why a value is being rejected before they submit it.</summary>
    public string? Hint => string.IsNullOrEmpty(Definition.Pattern)
        ? null
        : $"must match {Definition.Pattern}";
}

public sealed partial class WidgetViewModel : ObservableObject
{
    private readonly CommandStore _store;
    private readonly CommandRunner _runner;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private CommandItemViewModel? _selected;
    [ObservableProperty] private PaletteMode _mode = PaletteMode.Browsing;
    [ObservableProperty] private RunViewModel? _currentRun;
    [ObservableProperty] private CommandItemViewModel? _pending;
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel))]
    private PendingAction _pendingAction = PendingAction.Run;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Label of the confirm pane's primary button; it does four different things.</summary>
    public string ConfirmLabel => PendingAction switch
    {
        PendingAction.Approve => "Approve and run",
        PendingAction.Pin => "Pin (asks for administrator)",
        PendingAction.Unpin => "Unpin (asks for administrator)",
        _ => "Run",
    };

    public WidgetViewModel(CommandStore store, CommandRunner runner)
    {
        _store = store;
        _runner = runner;

        _store.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    public ObservableCollection<CommandItemViewModel> Items { get; } = [];
    public ObservableCollection<ParameterInputViewModel> Parameters { get; } = [];

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Rebuilds the visible list from the store, honouring the current search.</summary>
    public void Refresh()
    {
        var previousId = Selected?.Id;

        var matches = _store.Commands
            .Select(c => (Command: c, Score: string.IsNullOrWhiteSpace(SearchText)
                ? 0
                : FuzzyMatcher.ScoreBest(SearchText.Trim(), c.Name, c.Description, c.Group)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score!.Value)
            .ThenBy(x => x.Command.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new CommandItemViewModel(x.Command))
            .ToList();

        Items.Clear();
        foreach (var item in matches) Items.Add(item);

        Selected = Items.FirstOrDefault(i => i.Id == previousId) ?? Items.FirstOrDefault();

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    // ---- navigation ----

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        index = Math.Clamp(index + delta, 0, Items.Count - 1);
        Selected = Items[index];
    }

    [RelayCommand]
    public void Back()
    {
        Mode = PaletteMode.Browsing;
        Pending = null;
        PendingAction = PendingAction.Run;
        Parameters.Clear();
        StatusMessage = null;
    }

    // ---- running ----

    [RelayCommand]
    public void Activate(CommandItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;

        // An unapproved command is a button someone else put here. Running it on the first
        // click is exactly what the approval gate exists to prevent.
        if (item.NeedsApproval)
        {
            Pending = item;
            PendingAction = PendingAction.Approve;
            Mode = PaletteMode.Confirming;
            StatusMessage = $"{item.SourceText}. Approve it before it can run.";
            return;
        }

        if (item.HasParameters)
        {
            Pending = item;
            Parameters.Clear();
            foreach (var p in item.Command.Parameters) Parameters.Add(new ParameterInputViewModel(p));
            Mode = PaletteMode.CollectingParameters;
            return;
        }

        if (item.Command.ConfirmBeforeRun)
        {
            Pending = item;
            PendingAction = PendingAction.Run;
            Mode = PaletteMode.Confirming;
            StatusMessage = "This command asks for confirmation before it runs.";
            return;
        }

        Execute(item, null);
    }

    [RelayCommand]
    private void SubmitParameters()
    {
        if (Pending is null) return;

        Execute(Pending, Parameters.ToDictionary(p => p.Name, p => p.Value));
    }

    // ---- pinning ----

    /// <summary>
    /// Asks first, then elevates. Pinning is the one action here that permanently removes a
    /// consent dialog, so it gets its own explanation rather than going straight to UAC.
    /// </summary>
    [RelayCommand]
    private void Pin(CommandItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;

        if (item.IsPinned)
        {
            Unpin(item);
            return;
        }

        if (item.NeedsApproval)
        {
            StatusMessage = "Approve this command before pinning it.";
            return;
        }

        Pending = item;
        PendingAction = PendingAction.Pin;
        Mode = PaletteMode.Confirming;
        StatusMessage = PinService.Warning(item.Command);
    }

    [RelayCommand]
    private void Unpin(CommandItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || !item.IsPinned) return;

        Pending = item;
        PendingAction = PendingAction.Unpin;
        Mode = PaletteMode.Confirming;
        StatusMessage =
            $"Unpin \"{item.Name}\"? It stops running as administrator without a prompt. " +
            "This needs administrator too.";
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Pending is null) return;

        var item = Pending;

        switch (PendingAction)
        {
            case PendingAction.Approve:
                await _store.UpsertAsync(item.Command with { Approved = true });
                Refresh();

                var approved = Items.FirstOrDefault(i => i.Id == item.Id);
                if (approved is not null)
                {
                    Back();
                    Activate(approved);
                }

                return;

            case PendingAction.Pin:
                await ElevateAsync(() => PinService.Pin(item.Command));
                return;

            case PendingAction.Unpin:
                await ElevateAsync(() => PinService.Unpin(item.Id));
                return;

            default:
                Execute(item, null);
                return;
        }
    }

    /// <summary>
    /// Runs something that raises a UAC prompt, off the UI thread. On the UI thread the
    /// widget would freeze — repainting nothing — for as long as the dialog is up.
    /// </summary>
    private async Task ElevateAsync(Func<InstallResult> action)
    {
        IsBusy = true;
        try
        {
            var result = await Task.Run(action);

            StatusMessage = result.Message;

            if (!result.Success) return;

            // The pinned store is watched, but the watcher debounces and this is a direct
            // consequence of a click; reload now so the badge changes immediately.
            await _store.ReloadAsync();
            Refresh();

            Mode = PaletteMode.Browsing;
            Pending = null;
            PendingAction = PendingAction.Run;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(CommandItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;

        if (item.Command.Origin == CommandOrigin.Pinned)
        {
            StatusMessage = "Pinned commands are removed by an administrator, not from here.";
            return;
        }

        await _store.DeleteAsync(item.Id);
        Refresh();
    }

    /// <summary>
    /// Runs a command on behalf of the HTTP API. It goes through exactly the same runner,
    /// approval check and output panel as a click — there is one execution path, not two.
    /// Must be called on the UI thread.
    /// </summary>
    public RunViewModel StartForApi(CommandDef command, IReadOnlyDictionary<string, string>? arguments)
    {
        var run = new RunViewModel(_runner.Start(command, arguments));
        CurrentRun = run;
        Mode = PaletteMode.Output;
        StatusMessage = null;
        return run;
    }

    private void Execute(CommandItemViewModel item, IReadOnlyDictionary<string, string>? arguments)
    {
        try
        {
            var handle = _runner.Start(item.Command, arguments);
            CurrentRun = new RunViewModel(handle);
            Mode = PaletteMode.Output;
            StatusMessage = null;
        }
        catch (ParameterValidationException ex)
        {
            // Stay on the form so the value can be corrected rather than retyped.
            StatusMessage = ex.Message;
            Mode = PaletteMode.CollectingParameters;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            Mode = PaletteMode.Browsing;
        }
    }
}
