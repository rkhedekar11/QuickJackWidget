using System.Text.Json;
using System.Text.Json.Serialization;
using QuickJack.Core.Models;

namespace QuickJack.Core.Storage;

/// <summary>On-disk shape of a command store.</summary>
public sealed record CommandFile
{
    public int Version { get; init; } = 1;
    public List<CommandDef> Commands { get; init; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandFile))]
[JsonSerializable(typeof(CommandDef))]
[JsonSerializable(typeof(ParameterDef))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

/// <summary>Atomic, whole-file read/write of a <see cref="CommandFile"/>.</summary>
internal static class CommandFileIo
{
    public static CommandFile Read(string path)
    {
        if (!File.Exists(path)) return new CommandFile();

        // A store being rewritten by another process can briefly be locked; the caller retries.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length == 0) return new CommandFile();
        return JsonSerializer.Deserialize(stream, StoreJsonContext.Default.CommandFile)
               ?? new CommandFile();
    }

    /// <summary>
    /// Write via a temp file and swap it in, so a crash or a concurrent reader never
    /// observes a half-written store.
    /// </summary>
    public static void Write(string path, CommandFile file)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, StoreJsonContext.Default.CommandFile);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
