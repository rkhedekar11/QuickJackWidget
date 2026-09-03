using System.Windows;
using System.Windows.Threading;
using QuickJack.Api;
using QuickJack.App.Services;
using QuickJack.App.ViewModels;
using QuickJack.App.Views;
using QuickJack.Core.Execution;
using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.App;

public partial class App : Application
{
    private CommandStore? _store;
    private SettingsStore? _settings;
    private TrayIcon? _tray;
    private HotKeyService? _hotKeys;
    private WidgetWindow? _widget;
    private ApiHost? _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            // OnStartup is async void, so nothing else will ever observe this.
            Log.Error("Startup failed", ex);
            MessageBox.Show(
                "QuickJack could not start.\n\n" + ex.Message,
                "QuickJack", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {
        var paths = QuickJackPaths.Default;
        paths.EnsureUserDirectory();

        Log.Initialise(paths);
        Log.Info("Starting up.");

        // Scripts from a run that crashed before its cleanup would otherwise accumulate
        // in %TEMP% indefinitely.
        ScriptWriter.CleanStale(TimeSpan.FromHours(6));

        _settings = new SettingsStore(paths);
        _settings.Load();

        _store = new CommandStore(paths);
        await _store.ReloadAsync();
        _store.StartWatching();

        if (_store.Commands.Count == 0) await SeedExamplesAsync(_store);
        Log.Info($"Loaded {_store.Commands.Count} commands.");

        _tray = new TrayIcon();
        _tray.QuitRequested += (_, _) => Shutdown();
        Log.Info("Tray icon created.");

        // The agent runner is always wired up; it reports plainly if no agent is installed,
        // which beats a pinned command failing with nothing to explain it.
        var viewModel = new WidgetViewModel(_store, new CommandRunner(new AgentRunner()));
        _widget = new WidgetWindow(viewModel, _settings);
        _widget.Show();
        Log.Info("Widget shown.");

        _tray.ShowRequested += (_, _) => _widget.Expand();
        _tray.SettingsRequested += (_, _) => _widget.Expand();

        if (_settings.Current.ApiEnabled) await StartApiAsync(paths, viewModel);

        _hotKeys = new HotKeyService();
        _hotKeys.Pressed += (_, _) => _widget.Toggle();

        if (!_hotKeys.Attach(_widget, _settings.Current.Hotkey))
        {
            // Never fail silently here: the user presses the key, nothing happens, and
            // nothing on screen explains why.
            Log.Error(_hotKeys.LastError ?? "Hot key registration failed.");
            _tray.Notify("QuickJack", _hotKeys.LastError ?? "The hot key could not be registered.");
        }
        else
        {
            Log.Info("Hot key " + _hotKeys.ActiveGesture + " registered.");
            if (_hotKeys.LastError is { } note) _tray.Notify("QuickJack", note);
        }
    }

    private async Task StartApiAsync(QuickJackPaths paths, WidgetViewModel viewModel)
    {
        try
        {
            _api = new ApiHost(new ApiOptions
            {
                Paths = paths,
                Store = _store!,
                Dispatcher = new WidgetRunDispatcher(viewModel, Dispatcher),
                Port = _settings!.Current.ApiPort,
                RequireApproval = () => _settings!.Current.RequireApprovalForApiCommands,
            });

            await _api.StartAsync();
            Log.Info($"API listening on http://127.0.0.1:{_api.Port}");
        }
        catch (Exception ex)
        {
            // The widget is perfectly usable without the API, so a port clash should not
            // stop the app — but it must be visible, not silently missing.
            Log.Error("Could not start the API", ex);
            _api = null;
            _tray?.Notify("QuickJack",
                $"The command API could not start on port {_settings!.Current.ApiPort}. " +
                "The widget still works; see app.log.");
        }
    }

    /// <summary>
    /// A brand new install with an empty palette gives no clue what the tool does, so it
    /// starts with a couple of harmless read-only examples.
    /// </summary>
    private static async Task SeedExamplesAsync(CommandStore store)
    {
        await store.UpsertAsync(new CommandDef
        {
            Id = "ip-configuration",
            Name = "IP configuration",
            Description = "ipconfig /all",
            Icon = "🌐",
            Group = "Network",
            Shell = ShellKind.Cmd,
            Script = "ipconfig /all",
        });

        await store.UpsertAsync(new CommandDef
        {
            Id = "top-processes",
            Name = "Top processes by memory",
            Description = "The ten hungriest processes right now",
            Icon = "📊",
            Group = "System",
            Shell = ShellKind.PowerShell,
            Script = "Get-Process | Sort-Object WS -Descending | " +
                     "Select-Object -First 10 Name, @{n='MB';e={[int]($_.WS/1MB)}} | Format-Table -AutoSize",
        });
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A background widget that vanishes without a word is worse than one that says why.
        Log.Error("Unhandled dispatcher exception", e.Exception);

        MessageBox.Show(
            "QuickJack hit an unexpected error and will keep running.\n\n" + e.Exception.Message,
            "QuickJack", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Shutting down.");
        _api?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _hotKeys?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        base.OnExit(e);
    }
}
