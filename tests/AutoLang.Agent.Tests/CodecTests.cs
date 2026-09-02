using System.Buffers.Binary;
using System.Text;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

public class CodecTests
{
    private static MemoryStream Framed(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void A_written_message_reads_back_identically()
    {
        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, """{"type":"signal"}""");
        stream.Position = 0;

        Assert.Equal("""{"type":"signal"}""", NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void Several_messages_survive_one_stream()
    {
        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, "first");
        NativeMessagingCodec.Write(stream, "second");
        NativeMessagingCodec.Write(stream, "third");
        stream.Position = 0;

        Assert.Equal("first", NativeMessagingCodec.Read(stream));
        Assert.Equal("second", NativeMessagingCodec.Read(stream));
        Assert.Equal("third", NativeMessagingCodec.Read(stream));
        Assert.Null(NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void Hebrew_survives_the_round_trip()
    {
        // The length prefix counts BYTES, not characters. Hebrew is two bytes per letter in UTF-8,
        // so a codec that confuses the two desynchronises the stream on the very first message.
        const string json = """{"note":"שלום עולם"}""";

        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, json);
        stream.Position = 0;

        Assert.Equal(json, NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void Emoji_survive_the_round_trip()
    {
        const string json = """{"note":"👨‍👩‍👧‍👦🇮🇱"}""";

        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, json);
        stream.Position = 0;

        Assert.Equal(json, NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void The_header_is_little_endian()
    {
        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, "ab");

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00 }, bytes[..4]);
    }

    [Fact]
    public void End_of_stream_reads_as_null_rather_than_throwing()
    {
        // This is how the Bridge learns the browser closed the port. It must be an ordinary
        // return value, not an exception, or every clean shutdown looks like a crash.
        Assert.Null(NativeMessagingCodec.Read(new MemoryStream()));
    }

    [Fact]
    public void An_empty_message_is_legal()
    {
        var stream = new MemoryStream();
        NativeMessagingCodec.Write(stream, "");
        stream.Position = 0;

        Assert.Equal("", NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void A_truncated_payload_is_an_error_not_a_short_read()
    {
        // Promising 100 bytes and supplying 5 means the stream is desynchronised. Returning the
        // 5 bytes would let a partial message be parsed as a whole one.
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        stream.Write(header);
        stream.Write("short"u8);
        stream.Position = 0;

        Assert.Throws<EndOfStreamException>(() => NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void A_truncated_header_is_an_error()
    {
        var stream = new MemoryStream([0x01, 0x02]);

        Assert.Throws<EndOfStreamException>(() => NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void An_oversized_declared_length_is_refused_before_allocating()
    {
        // Without this, a hostile or desynchronised stream declaring 2GB would have us allocate it.
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        stream.Write(header);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void A_negative_declared_length_is_refused()
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, -1);
        stream.Write(header);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => NativeMessagingCodec.Read(stream));
    }

    [Fact]
    public void Writing_more_than_the_limit_is_refused()
    {
        var stream = new MemoryStream();
        var oversized = new string('x', Wire.MaxMessageBytes + 1);

        Assert.Throws<InvalidDataException>(() => NativeMessagingCodec.Write(stream, oversized));
    }

    [Fact]
    public async Task A_stream_that_returns_one_byte_at_a_time_still_works()
    {
        // A pipe is free to hand back fewer bytes than asked for. A codec that assumes a single
        // Read fills the buffer works in tests and fails under load, which is the worst time to
        // discover it.
        var source = Framed("""{"type":"signal","conversationKey":"abc"}""");
        var dribbling = new DribbleStream(source.ToArray());

        Assert.Equal("""{"type":"signal","conversationKey":"abc"}""", await NativeMessagingCodec.ReadAsync(dribbling));
    }

    /// <summary>Returns at most one byte per read, the worst legal behaviour for a stream.</summary>
    private sealed class DribbleStream(byte[] data) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0) return 0;
            buffer[offset] = data[_position++];
            return 1;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length || buffer.Length == 0) return 0;
            buffer[0] = data[_position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
