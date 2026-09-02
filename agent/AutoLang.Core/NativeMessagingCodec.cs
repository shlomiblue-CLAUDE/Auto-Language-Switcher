using System.Buffers.Binary;
using System.Text;

namespace AutoLang.Core;

/// <summary>
/// Chrome's native messaging framing: a 32-bit little-endian byte count, then that many bytes of
/// UTF-8 JSON.
///
/// Two rules this format punishes you for breaking. Reads must be looped - a pipe is free to hand
/// back fewer bytes than asked for, and a single Read that assumes otherwise fails only under load,
/// which is the worst time to find out. And stdout belongs exclusively to framed messages: one
/// stray Console.WriteLine corrupts the stream and Chrome kills the host with no useful error. All
/// diagnostics go to stderr.
/// </summary>
public static class NativeMessagingCodec
{
    /// <summary>Reads one message, or null at end of stream.</summary>
    public static string? Read(Stream stream, int maxBytes = Wire.MaxMessageBytes)
    {
        Span<byte> header = stackalloc byte[4];
        if (!TryReadExactly(stream, header)) return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);

        // A negative or oversized length is either a desynchronised stream or something hostile.
        // Either way the stream cannot be trusted from here on.
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException($"Declared message length {length} is outside 0..{maxBytes}.");

        if (length == 0) return string.Empty;

        var buffer = new byte[length];
        if (!TryReadExactly(stream, buffer))
            throw new EndOfStreamException($"Stream ended after {length} bytes were promised.");

        return Encoding.UTF8.GetString(buffer);
    }

    public static async Task<string?> ReadAsync(Stream stream, int maxBytes = Wire.MaxMessageBytes, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await TryReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException($"Declared message length {length} is outside 0..{maxBytes}.");

        if (length == 0) return string.Empty;

        var buffer = new byte[length];
        if (!await TryReadExactlyAsync(stream, buffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException($"Stream ended after {length} bytes were promised.");

        return Encoding.UTF8.GetString(buffer);
    }

    public static void Write(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > Wire.MaxMessageBytes)
            throw new InvalidDataException($"Message of {payload.Length} bytes exceeds the {Wire.MaxMessageBytes} byte limit.");

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > Wire.MaxMessageBytes)
            throw new InvalidDataException($"Message of {payload.Length} bytes exceeds the {Wire.MaxMessageBytes} byte limit.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Stream ended mid-message.");
            read += n;
        }
        return true;
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Stream ended mid-message.");
            read += n;
        }
        return true;
    }
}
