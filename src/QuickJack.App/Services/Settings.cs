using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickJack.Core.Storage;

namespace QuickJack.App.Services;

public sealed record Settings
{
    /// <summary>Orb position in physical screen pixels, or null on first run.</summary>
    public int? OrbX { get; set; }
    public int? OrbY { get; set; }

    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public bool AutoStart { get; set; }

    /// <summary>
    /// API-registered commands must be approved once before they can run. Defaults on: it is
    /// what stops a rogue caller planting a button the user clicks without thinking.
    /// </summary>
    public bool RequireApprovalForApiCommands { get; set; } = true;

    public bool ShowOutputPanel { get; set; } = true;
    public int ApiPort { get; set; } = 47821;
    public bool ApiEnabled { get; set; } = true;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsStore(QuickJackPaths paths)
{
    private readonly Lock _gate = new();

    public Settings Current { get; private set; } = new();

    public Settings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(paths.Settings))
                {
                    using var stream = File.OpenRead(paths.Settings);
                    Current = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.Settings)
                              ?? new Settings();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt settings should not stop the app from starting; defaults are fine.
                Current = new Settings();
            }

            return Current;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                paths.EnsureUserDirectory();
                var temp = paths.Settings + ".tmp";
                using (var stream = File.Create(temp))
                    JsonSerializer.Serialize(stream, Current, SettingsJsonContext.Default.Settings);

                if (File.Exists(paths.Settings)) File.Replace(temp, paths.Settings, null, true);
                else File.Move(temp, paths.Settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a window position is not worth crashing over.
            }
        }
    }

    public void Update(Action<Settings> change)
    {
        change(Current);
        Save();
    }
}
