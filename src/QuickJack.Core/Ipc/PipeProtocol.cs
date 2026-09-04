using System.Buffers.Binary;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickJack.Core.Ipc;

/// <summary>
/// Messages between the widget and the elevated agent.
/// <para>
/// A run request carries a command <em>id</em> and nothing else executable. The agent
/// resolves it against its own copy of the admin-owned pinned store, so a compromised widget
/// process cannot turn the agent into an arbitrary elevated shell — which is the entire
/// reason the agent is defensible.
/// </para>
/// </summary>
public enum PipeMessageKind
{
    Run,
    Cancel,
    Line,
    Done,
    Error,
}

public sealed record PipeMessage
{
    public PipeMessageKind Kind { get; init; }
    public string? RunId { get; init; }

    /// <summary>Id of a command in the pinned store. Never script text.</summary>
    public string? CommandId { get; init; }

    public Dictionary<string, string>? Arguments { get; init; }

    public string? Stream { get; init; }
    public string? Text { get; init; }
    public string? State { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationMs { get; init; }
}

[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeMessage))]
internal sealed partial class PipeJsonContext : JsonSerializerContext;

/// <summary>
/// Length-prefixed JSON frames. A pipe is a byte stream with no message boundaries of its
/// own, so the length prefix is what stops two messages being read as one.
/// </summary>
public static class PipeProtocol
{
    /// <summary>Frames larger than this are refused rather than allocated.</summary>
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>
    /// Pipe buffer size, which the server end sets for both directions.
    /// <para>
    /// It must not be left at the default of zero. With no buffer, Windows completes a write
    /// only once the peer has read it — so writing to a peer that has stopped reading (the
    /// agent rejecting a client, or a client that has been cancelled) blocks forever rather
    /// than failing. With a buffer, an ordinary frame is handed over and the write returns.
    /// </para>
    /// </summary>
    public const int BufferBytes = 64 * 1024;

    /// <summary>Per-user pipe name, so two logged-in users never share an agent.</summary>
    public static string PipeName(string? userSid = null)
    {
        userSid ??= WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        return $"QuickJack.Agent.{userSid}";
    }

    public static async Task WriteAsync(Stream stream, PipeMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, PipeJsonContext.Default.PipeMessage);
        if (json.Length > MaxFrameBytes)
            throw new InvalidOperationException($"Message of {json.Length} bytes exceeds the frame limit.");

        // One write, not prefix-then-payload: a single call cannot be interleaved with
        // another writer's, and a frame that is half-written is unrecoverable.
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(4));

        // Deliberately no FlushAsync. On a named pipe that is FlushFileBuffers, which blocks
        // until the peer has read everything written — so writing to a peer that has stopped
        // reading (the agent rejecting a client, say) would hang the writer for good. The
        // write itself has already handed the bytes to the pipe; there is nothing to flush.
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or null when the peer closes the pipe.</summary>
    public static async Task<PipeMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var prefix = new byte[4];
        if (!await ReadExactlyAsync(stream, prefix, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

        // A hostile or corrupt length must not become a 2 GB allocation.
        if (length is <= 0 or > MaxFrameBytes)
            throw new InvalidOperationException($"Refusing a frame claiming {length} bytes.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false)) return null;

        try
        {
            return JsonSerializer.Deserialize(payload, PipeJsonContext.Default.PipeMessage);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Malformed message on the pipe.", ex);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (got == 0) return false;
            read += got;
        }

        return true;
    }

    public static string Describe(PipeMessage message) => message.Kind switch
    {
        PipeMessageKind.Run => $"run {message.CommandId}",
        PipeMessageKind.Cancel => $"cancel {message.RunId}",
        _ => message.Kind.ToString().ToLowerInvariant(),
    };

    internal static readonly Encoding Utf8 = new UTF8Encoding(false);
}
