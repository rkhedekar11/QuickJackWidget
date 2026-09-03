using QuickJack.Core.Models;

namespace QuickJack.Core.Storage;

/// <summary>
/// The single writer for command state, merging the user store with the admin-owned
/// pinned store. Reads are lock-free against an immutable snapshot; writes are
/// serialised and land atomically.
/// </summary>
public sealed class CommandStore : IDisposable
{
    private const int ReadRetries = 5;
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly QuickJackPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _debounceLock = new();

    private IReadOnlyList<CommandDef> _snapshot = [];
    private Timer? _debounce;
    private bool _disposed;

    public CommandStore(QuickJackPaths paths) => _paths = paths;

    /// <summary>Raised after the merged snapshot changes, possibly from a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Immutable merged view: user commands plus admin-pinned ones.</summary>
    public IReadOnlyList<CommandDef> Commands => Volatile.Read(ref _snapshot);

    public CommandDef? Find(string id) =>
        Commands.FirstOrDefault(c => IdEquals(c.Id, id));

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { RebuildSnapshot(); }
        finally { _gate.Release(); }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Create or replace a command in the user store.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The command asks for <see cref="ElevationMode.Agent"/>, or its id is owned by the
    /// pinned store. Agent-mode commands are only legal in the admin-ACL'd pinned store:
    /// allowing one here would make no-prompt elevation writable by anything running as
    /// the user, which is precisely what the split store exists to prevent.
    /// </exception>
    public async Task<CommandDef> UpsertAsync(CommandDef command, CancellationToken ct = default)
    {
        if (command.Elevation == ElevationMode.Agent)
        {
            throw new InvalidOperationException(
                "Agent-elevated commands cannot live in the user store; pin them instead.");
        }

        CommandDef saved;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = ReadWithRetry(_paths.UserCommands);
            var pinned = ReadWithRetry(_paths.PinnedCommands);

            var id = command.Id;
            if (!Slug.IsValid(id))
            {
                var existing = user.Commands.Select(c => c.Id)
                    .Concat(pinned.Commands.Select(c => c.Id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                id = Slug.Unique(command.Name, existing.Contains);
            }
            else if (pinned.Commands.Any(c => IdEquals(c.Id, id)))
            {
                throw new InvalidOperationException($"Command id {id} is owned by the pinned store.");
            }

            var index = user.Commands.FindIndex(c => IdEquals(c.Id, id));
            var now = DateTimeOffset.UtcNow;

            saved = command with
            {
                Id = id,
                CreatedAt = index >= 0 ? user.Commands[index].CreatedAt : now,
                UpdatedAt = now,
                Origin = CommandOrigin.User,
            };

            if (index >= 0) user.Commands[index] = saved;
            else user.Commands.Add(saved);

            _paths.EnsureUserDirectory();
            CommandFileIo.Write(_paths.UserCommands, user);
            RebuildSnapshot();
        }
        finally { _gate.Release(); }

        // Raised directly as well as via the watcher, so an in-process write never depends
        // on filesystem notification latency.
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = ReadWithRetry(_paths.UserCommands);
            if (user.Commands.RemoveAll(c => IdEquals(c.Id, id)) == 0) return false;

            CommandFileIo.Write(_paths.UserCommands, user);
            RebuildSnapshot();
        }
        finally { _gate.Release(); }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Watch both stores so edits made outside the app show up live.</summary>
    public void StartWatching()
    {
        Watch(_paths.UserDirectory, Path.GetFileName(_paths.UserCommands));
        Watch(_paths.MachineDirectory, Path.GetFileName(_paths.PinnedCommands));
    }

    private void Watch(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Watching is a convenience; the app still works if the OS refuses a handle.
        }
    }

    // An atomic swap fires several events in a burst, so collapse them into one reload.
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_debounceLock)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RebuildSnapshot()
    {
        var user = ReadWithRetry(_paths.UserCommands).Commands
            .Select(c => c with { Origin = CommandOrigin.User });
        var pinned = ReadWithRetry(_paths.PinnedCommands).Commands
            .Select(c => c with { Origin = CommandOrigin.Pinned });

        // Pinned wins on a collision: it is the store a non-admin process cannot forge.
        var merged = new Dictionary<string, CommandDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in user) merged[c.Id] = c;
        foreach (var c in pinned) merged[c.Id] = c;

        Volatile.Write(ref _snapshot, merged.Values
            .OrderBy(c => c.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    // The window between File.Replace unlinking and relinking is tiny but real.
    private static CommandFile ReadWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return CommandFileIo.Read(path); }
            catch (IOException) when (attempt < ReadRetries) { Thread.Sleep(20 * (attempt + 1)); }
            catch (System.Text.Json.JsonException) { return new CommandFile(); }
            catch (UnauthorizedAccessException) { return new CommandFile(); }
        }
    }

    private static bool IdEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_debounceLock)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
        }

        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _gate.Dispose();
    }
}
