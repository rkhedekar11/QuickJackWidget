using System.Buffers.Binary;
using System.Text;
using QuickJack.Core.Ipc;

namespace QuickJack.Core.Tests;

/// <summary>
/// The framing between the widget and the elevated agent. A pipe is a byte stream with no
/// message boundaries of its own, so everything here is about the length prefix: that it
/// separates messages, and that a hostile one cannot be turned into a huge allocation.
/// </summary>
public class PipeProtocolTests
{
    private static async Task<PipeMessage?> RoundTripAsync(PipeMessage message)
    {
        var stream = new MemoryStream();
        await PipeProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        return await PipeProtocol.ReadAsync(stream);
    }

    private static MemoryStream Framed(int declaredLength, byte[] payload)
    {
        var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, declaredLength);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_run_request_survives_the_round_trip()
    {
        var sent = new PipeMessage
        {
            Kind = PipeMessageKind.Run,
            RunId = "abc123",
            CommandId = "flush-dns",
            Arguments = new Dictionary<string, string> { ["HOST"] = "example.com" },
        };

        var received = await RoundTripAsync(sent);

        Assert.NotNull(received);
        Assert.Equal(PipeMessageKind.Run, received.Kind);
        Assert.Equal("abc123", received.RunId);
        Assert.Equal("flush-dns", received.CommandId);
        Assert.Equal("example.com", received.Arguments?["HOST"]);
    }

    [Fact]
    public async Task A_done_reply_survives_the_round_trip()
    {
        var received = await RoundTripAsync(new PipeMessage
        {
            Kind = PipeMessageKind.Done,
            RunId = "abc123",
            State = "Succeeded",
            ExitCode = 0,
            DurationMs = 12.5,
        });

        Assert.NotNull(received);
        Assert.Equal(PipeMessageKind.Done, received.Kind);
        Assert.Equal("Succeeded", received.State);
        Assert.Equal(0, received.ExitCode);
        Assert.Equal(12.5, received.DurationMs);
    }

    [Fact]
    public async Task Consecutive_messages_are_read_one_at_a_time()
    {
        // The whole reason for the length prefix: without it these two would be read as one
        // malformed blob.
        var stream = new MemoryStream();
        await PipeProtocol.WriteAsync(stream, new PipeMessage { Kind = PipeMessageKind.Line, Text = "one" });
        await PipeProtocol.WriteAsync(stream, new PipeMessage { Kind = PipeMessageKind.Line, Text = "two" });
        stream.Position = 0;

        Assert.Equal("one", (await PipeProtocol.ReadAsync(stream))?.Text);
        Assert.Equal("two", (await PipeProtocol.ReadAsync(stream))?.Text);
        Assert.Null(await PipeProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task Non_ascii_text_survives_the_round_trip()
    {
        var received = await RoundTripAsync(new PipeMessage
        {
            Kind = PipeMessageKind.Line,
            Text = "héllo — 日本語",
        });

        Assert.Equal("héllo — 日本語", received?.Text);
    }

    [Fact]
    public async Task A_closed_stream_reads_as_null_rather_than_throwing()
    {
        // The peer hanging up is ordinary, not an error.
        Assert.Null(await PipeProtocol.ReadAsync(new MemoryStream()));
    }

    [Fact]
    public async Task A_truncated_payload_reads_as_null()
    {
        var stream = new MemoryStream();
        await PipeProtocol.WriteAsync(stream, new PipeMessage { Kind = PipeMessageKind.Line, Text = "hello" });

        var truncated = new MemoryStream(stream.ToArray()[..^3]);

        Assert.Null(await PipeProtocol.ReadAsync(truncated));
    }

    [Fact]
    public async Task An_oversized_length_prefix_is_refused_without_allocating_it()
    {
        var stream = Framed(int.MaxValue, [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipeProtocol.ReadAsync(stream));

        Assert.Contains("Refusing", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PipeProtocol.MaxFrameBytes + 1)]
    public async Task An_out_of_range_length_prefix_is_refused(int length)
    {
        var stream = Framed(length, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PipeProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task A_malformed_payload_is_reported_as_a_protocol_error()
    {
        var garbage = Encoding.UTF8.GetBytes("{ not json");
        var stream = Framed(garbage.Length, garbage);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipeProtocol.ReadAsync(stream));

        Assert.Contains("Malformed", ex.Message);
    }

    [Fact]
    public async Task Writing_an_oversized_message_throws_rather_than_sending_it()
    {
        var message = new PipeMessage
        {
            Kind = PipeMessageKind.Line,
            Text = new string('x', PipeProtocol.MaxFrameBytes + 1),
        };

        var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipeProtocol.WriteAsync(stream, message));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void The_pipe_name_is_per_user()
    {
        // Two logged-in users must never share an agent: one user's widget reaching the
        // other's elevated agent would be an escalation across accounts.
        Assert.NotEqual(PipeProtocol.PipeName("S-1-5-21-1"), PipeProtocol.PipeName("S-1-5-21-2"));
        Assert.Contains("S-1-5-21-1", PipeProtocol.PipeName("S-1-5-21-1"));
    }
}
