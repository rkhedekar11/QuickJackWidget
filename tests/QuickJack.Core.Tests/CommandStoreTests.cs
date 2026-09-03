using QuickJack.Core.Models;
using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

public class CommandStoreTests
{
    private static CommandDef Sample(string name = "Flush DNS", string id = "") => new()
    {
        Id = id,
        Name = name,
        Shell = ShellKind.Cmd,
        Script = "ipconfig /flushdns",
    };

    [Fact]
    public async Task Upsert_generates_a_slug_and_round_trips()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        var saved = await store.UpsertAsync(Sample());

        Assert.Equal("flush-dns", saved.Id);
        Assert.Equal(CommandOrigin.User, saved.Origin);
        Assert.NotEqual(default, saved.CreatedAt);

        // A second store reading the same files must see the same command.
        using var reopened = new CommandStore(root.Paths);
        await reopened.ReloadAsync();

        var loaded = Assert.Single(reopened.Commands);
        Assert.Equal("flush-dns", loaded.Id);
        Assert.Equal("ipconfig /flushdns", loaded.Script);
        Assert.Equal(ShellKind.Cmd, loaded.Shell);
    }

    [Fact]
    public async Task Upsert_with_a_new_name_does_not_collide()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        var first = await store.UpsertAsync(Sample());
        var second = await store.UpsertAsync(Sample());

        Assert.Equal("flush-dns", first.Id);
        Assert.Equal("flush-dns-2", second.Id);
        Assert.Equal(2, store.Commands.Count);
    }

    [Fact]
    public async Task Upsert_by_existing_id_replaces_and_preserves_CreatedAt()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        var first = await store.UpsertAsync(Sample());
        await Task.Delay(10);
        var updated = await store.UpsertAsync(first with { Script = "ipconfig /all" });

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(first.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt > first.UpdatedAt);
        Assert.Equal("ipconfig /all", Assert.Single(store.Commands).Script);
    }

    [Fact]
    public async Task Upsert_rejects_agent_elevation()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        // Agent mode is only legal in the admin-ACL'd pinned store. This is the last line
        // of defence behind the API returning 403 for the same thing.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Sample() with { Elevation = ElevationMode.Agent }));

        Assert.Empty(store.Commands);
    }

    [Fact]
    public async Task Upsert_cannot_shadow_a_pinned_id()
    {
        using var root = new TempRoot();
        WritePinned(root, Sample("Flush DNS", "flush-dns") with { Elevation = ElevationMode.Agent });

        using var store = new CommandStore(root.Paths);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(Sample() with { Id = "flush-dns", Script = "evil.exe" }));
    }

    [Fact]
    public async Task Pinned_wins_over_a_user_command_with_the_same_id()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        await store.UpsertAsync(Sample());

        // Simulate a command later pinned by an admin under an id the user store also holds.
        WritePinned(root, Sample("Flush DNS", "flush-dns") with
        {
            Script = "trusted.exe",
            Elevation = ElevationMode.Agent,
        });
        await store.ReloadAsync();

        var merged = Assert.Single(store.Commands);
        Assert.Equal(CommandOrigin.Pinned, merged.Origin);
        Assert.Equal("trusted.exe", merged.Script);
    }

    [Fact]
    public async Task Delete_removes_only_the_named_command()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        await store.UpsertAsync(Sample("Flush DNS"));
        await store.UpsertAsync(Sample("Restart IIS"));

        Assert.True(await store.DeleteAsync("flush-dns"));
        Assert.False(await store.DeleteAsync("flush-dns"));
        Assert.Equal("restart-iis", Assert.Single(store.Commands).Id);
    }

    [Fact]
    public async Task Concurrent_upserts_all_survive()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);

        // Every write rewrites the whole file, so a lost update would silently drop
        // commands. Serialisation plus the atomic swap is what prevents that.
        await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(i => store.UpsertAsync(Sample($"Command {i}"))));

        Assert.Equal(25, store.Commands.Count);

        using var reopened = new CommandStore(root.Paths);
        await reopened.ReloadAsync();
        Assert.Equal(25, reopened.Commands.Count);
    }

    [Fact]
    public async Task Corrupt_store_is_treated_as_empty_rather_than_throwing()
    {
        using var root = new TempRoot();
        await File.WriteAllTextAsync(root.Paths.UserCommands, "{ this is not json");

        using var store = new CommandStore(root.Paths);
        await store.ReloadAsync();

        Assert.Empty(store.Commands);
    }

    [Fact]
    public async Task Missing_store_files_are_fine()
    {
        using var root = new TempRoot();
        Directory.Delete(root.Paths.MachineDirectory, recursive: true);

        using var store = new CommandStore(root.Paths);
        await store.ReloadAsync();

        Assert.Empty(store.Commands);
    }

    [Fact]
    public async Task Find_is_case_insensitive()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);
        await store.UpsertAsync(Sample());

        Assert.NotNull(store.Find("FLUSH-DNS"));
        Assert.Null(store.Find("nope"));
    }

    [Fact]
    public async Task External_edits_are_picked_up_by_the_watcher()
    {
        using var root = new TempRoot();
        using var store = new CommandStore(root.Paths);
        await store.UpsertAsync(Sample());

        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();
        store.StartWatching();

        WritePinned(root, Sample("Restart IIS", "restart-iis"));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(store.Commands, c => c.Id == "restart-iis");
    }

    private static void WritePinned(TempRoot root, params CommandDef[] commands)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            commands = commands.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                shell = c.Shell.ToString(),
                script = c.Script,
                elevation = c.Elevation.ToString(),
            }),
        });
        File.WriteAllText(root.Paths.PinnedCommands, json);
    }
}
